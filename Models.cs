using System.Text.Json;

namespace LanFileHall;

public record PublishedFile(string Id, string Path);
public record FileOffer(string Id, string Name, long Size, DateTime ModifiedUtc, bool PreviousUpload = false, bool IsDirectory = false);
public record FolderEntry(string RelativePath, long Size, DateTime ModifiedUtc);
public record FolderManifest(string Id, List<string> Directories, List<FolderEntry> Files);
public record Catalog(string DeviceId, string DeviceName, List<FileOffer> Files);
public record Beacon(string Protocol, string DeviceId, string DeviceName, int Port);
public record Peer(string Id, string Name, string Host, int Port, DateTime SeenUtc);
public record HallRow(Peer Peer, FileOffer File, bool Mine);

public sealed class AppState
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public List<PublishedFile> Published { get; set; } = [];
}

public sealed class FileStore
{
    private readonly object gate = new();
    private readonly string statePath;
    private readonly AppState state;
    private readonly HashSet<string> previousUploads;
    public string DeviceId => state.DeviceId;
    public string DownloadDirectory { get; }

    public FileStore(string? root = null, string? downloadDirectory = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanFileHall");
        Directory.CreateDirectory(root);
        DownloadDirectory = downloadDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "局域网文件大厅");
        Directory.CreateDirectory(DownloadDirectory);
        statePath = Path.Combine(root, "state.json");
        try { state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(statePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { state = new(); }
        previousUploads = state.Published.Select(p => p.Id).ToHashSet();
        Save();
    }

    private void Save()
    {
        var temporary = statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, statePath, true);
    }

    public void Publish(IEnumerable<string> paths)
    {
        lock (gate)
        {
            foreach (var path in paths)
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if ((!File.Exists(full) && !Directory.Exists(full)) || state.Published.Any(p => p.Path.Equals(full, StringComparison.OrdinalIgnoreCase))) continue;
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("请发布实际文件或文件夹，不能发布符号链接或目录联接。" );
                state.Published.Add(new(Guid.NewGuid().ToString("N"), full));
            }
            Save();
        }
    }

    public void Unpublish(string id)
    {
        lock (gate) { state.Published.RemoveAll(p => p.Id == id); Save(); }
    }

    public Catalog GetCatalog()
    {
        lock (gate)
        {
            var files = new List<FileOffer>();
            foreach (var item in state.Published)
            {
                try
                {
                    var info = new FileInfo(item.Path);
                    if (info.Exists) files.Add(new(item.Id, info.Name, info.Length, info.LastWriteTimeUtc, previousUploads.Contains(item.Id)));
                    else if (Directory.Exists(item.Path) && (File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) == 0)
                    {
                        var directory = new DirectoryInfo(item.Path);
                        files.Add(new(item.Id, directory.Name.Length == 0 ? "共享文件夹" : directory.Name, 0, directory.LastWriteTimeUtc, previousUploads.Contains(item.Id), true));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return new(DeviceId, Environment.MachineName, files);
        }
    }

    public FileStream? OpenPublished(string id)
    {
        lock (gate)
        {
            var item = state.Published.FirstOrDefault(p => p.Id == id);
            if (item == null) return null;
            try
            {
                if ((File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) != 0) return null;
                return new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    private string? FolderRoot(string id)
    {
        lock (gate)
        {
            var item = state.Published.FirstOrDefault(p => p.Id == id);
            return item != null && Directory.Exists(item.Path) && (File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) == 0 ? item.Path : null;
        }
    }

    public FolderManifest? GetFolderManifest(string id, CancellationToken ct)
    {
        var root = FolderRoot(id);
        if (root == null) return null;
        var directories = new List<string>();
        var files = new List<FolderEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                // Links and junctions can lead outside the published folder.
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if ((attributes & FileAttributes.Directory) != 0) { directories.Add(relative); pending.Push(path); }
                else
                {
                    var info = new FileInfo(path);
                    files.Add(new(relative, info.Length, info.LastWriteTimeUtc));
                }
                if (directories.Count + files.Count > FolderPaths.MaxEntries)
                    throw new IOException("文件夹条目超过 50,000 个，请选择较小的子文件夹。" );
            }
        }
        return new(id, directories, files);
    }

    public FileStream? OpenFolderFile(string id, string relative, out DateTime modifiedUtc)
    {
        modifiedUtc = default;
        try
        {
            var root = FolderRoot(id);
            if (root == null) return null;
            var path = FolderPaths.Resolve(root, relative);
            string current = root;
            foreach (var segment in FolderPaths.Segments(relative))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
            }
            modifiedUtc = File.GetLastWriteTimeUtc(path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
