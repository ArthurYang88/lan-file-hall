using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LanFileHall;

internal static class FolderPaths
{
    public const int MaxEntries = 50000;
    public static string[] Segments(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || Path.IsPathRooted(relative))
            throw new IOException("无效的文件夹相对路径");
        var parts = relative.Split('/');
        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("无效的文件夹相对路径");
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
                throw new IOException("无效的文件夹文件名");
        }
        return parts;
    }

    public static string Resolve(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root);
        string result = Path.GetFullPath(Path.Combine([fullRoot, .. Segments(relative)]));
        string prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("文件路径超出共享目录");
        return result;
    }

    public static void DeleteStaging(string downloadRoot, string staging)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(downloadRoot));
        string target = Path.GetFullPath(staging);
        string name = Path.GetFileName(target);
        if (!string.Equals(Path.GetDirectoryName(target), fullRoot, StringComparison.OrdinalIgnoreCase) ||
            name.Length != 41 || name[0] != '.' || !name.EndsWith(".partial", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[1..33], "N", out _)) throw new IOException("临时目录清理路径无效");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}

public sealed partial class HallService
{
    private async Task<bool> ServeFolderRequest(string target, NetworkStream stream, CancellationTokenSource deadline)
    {
        if (!target.StartsWith("/folders/", StringComparison.Ordinal)) return false;
        var parts = target.Split('?', 2);
        var route = parts[0].Split('/');
        if (route.Length is not (3 or 4) || !Guid.TryParseExact(route[2], "N", out _))
        { await Reply(stream, 404, "Folder unavailable", deadline.Token); return true; }
        if (route.Length == 3 && parts.Length == 1)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            FolderManifest? manifest;
            try { manifest = Store.GetFolderManifest(route[2], deadline.Token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { await Reply(stream, 400, "Folder cannot be read: " + ex.Message, deadline.Token); return true; }
            if (manifest == null) { await Reply(stream, 404, "Folder unavailable", deadline.Token); return true; }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            if (bytes.Length > 16 * 1024 * 1024)
            { await Reply(stream, 400, "Folder manifest too large", deadline.Token); return true; }
            await WriteHeader(stream, 200, bytes.Length, "application/json", deadline.Token);
            await stream.WriteAsync(bytes, deadline.Token);
            return true;
        }
        if (route.Length == 4 && route[3] == "files" && parts.Length == 2 && parts[1].StartsWith("path=", StringComparison.Ordinal))
        {
            string relative;
            try { relative = Uri.UnescapeDataString(parts[1][5..]); }
            catch (UriFormatException) { await Reply(stream, 400, "Invalid path", deadline.Token); return true; }
            using var file = Store.OpenFolderFile(route[2], relative, out var modifiedUtc);
            if (file == null) { await Reply(stream, 404, "File unavailable", deadline.Token); return true; }
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {file.Length}\r\nContent-Type: application/octet-stream\r\nX-File-Modified: {modifiedUtc.Ticks}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, deadline.Token);
            var buffer = new byte[131072];
            int count;
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            while ((count = await file.ReadAsync(buffer, deadline.Token)) > 0)
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                await stream.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
            }
            return true;
        }
        await Reply(stream, 404, "Not found", deadline.Token);
        return true;
    }

    private async Task<string> DownloadFolder(Peer peer, FileOffer offer, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        string baseUrl = $"http://{peer.Host}:{peer.Port}/folders/{offer.Id}";
        using var response = await http.GetAsync(baseUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("文件夹不可用，请确认双方已更新到支持文件夹的版本。" );
        if (!response.IsSuccessStatusCode) throw new IOException("发送方无法读取此文件夹，请检查文件夹权限或选择较小的子文件夹。" );
        await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024).WaitAsync(timeout.Token);
        var manifest = JsonSerializer.Deserialize<FolderManifest>(await response.Content.ReadAsByteArrayAsync(timeout.Token))
            ?? throw new IOException("无效的文件夹列表");
        ValidateManifest(manifest, offer.Id);
        long total;
        try { total = manifest.Files.Aggregate(0L, (sum, f) => checked(sum + f.Size)); }
        catch (OverflowException) { throw new IOException("文件夹大小无效"); }

        string staging = Path.Combine(Store.DownloadDirectory, "." + Guid.NewGuid().ToString("N") + ".partial");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (string directory in manifest.Directories)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(FolderPaths.Resolve(staging, directory));
            }
            long done = 0;
            var lastUpdate = DateTime.MinValue;
            foreach (var entry in manifest.Files)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var fileResponse = await http.GetAsync(baseUrl + "/files?path=" + Uri.EscapeDataString(entry.RelativePath), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                fileResponse.EnsureSuccessStatusCode();
                if (fileResponse.Content.Headers.ContentLength != entry.Size ||
                    !fileResponse.Headers.TryGetValues("X-File-Modified", out var timestamps) ||
                    !long.TryParse(timestamps.SingleOrDefault(), out long ticks) || ticks != entry.ModifiedUtc.Ticks)
                    throw new IOException("文件夹中的文件已变化，请重新下载：" + entry.RelativePath);
                string destination = FolderPaths.Resolve(staging, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                long received = 0;
                await using (var input = await fileResponse.Content.ReadAsStreamAsync(timeout.Token))
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                {
                    var buffer = new byte[131072];
                    int count;
                    while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        received += count;
                        if (received > entry.Size) throw new IOException("文件内容超过声明大小");
                        await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                        done += count;
                        if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds >= 100 || done == total)
                        { progress?.Report((done, total)); lastUpdate = DateTime.UtcNow; }
                    }
                }
                if (received != entry.Size) throw new IOException("文件夹传输中断，文件不完整：" + entry.RelativePath);
                File.SetLastWriteTimeUtc(destination, entry.ModifiedUtc);
            }
            ct.ThrowIfCancellationRequested();
            string name = SafeName(offer.Name);
            for (int i = 0; ; i++)
            {
                string candidate = Path.Combine(Store.DownloadDirectory, i == 0 ? name : $"{name} ({i})");
                try
                {
                    Directory.Move(staging, candidate);
                    progress?.Report((total, total));
                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
            }
        }
        finally { FolderPaths.DeleteStaging(Store.DownloadDirectory, staging); }
    }

    internal static void ValidateManifest(FolderManifest manifest, string id)
    {
        if (manifest.Id != id || manifest.Files == null || manifest.Directories == null ||
            manifest.Files.Count + manifest.Directories.Count > FolderPaths.MaxEntries)
            throw new IOException("无效的文件夹列表");
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in manifest.Directories)
        {
            FolderPaths.Segments(directory);
            if (!directories.Add(directory)) throw new IOException("文件夹列表含重复路径");
        }
        foreach (var file in manifest.Files)
        {
            if (file == null || file.Size < 0) throw new IOException("文件夹列表含无效文件");
            FolderPaths.Segments(file.RelativePath);
            if (!files.Add(file.RelativePath) || directories.Contains(file.RelativePath)) throw new IOException("文件夹列表含重复路径");
        }
        foreach (string path in directories.Concat(files))
        {
            int slash = path.IndexOf('/');
            while (slash >= 0)
            {
                if (files.Contains(path[..slash])) throw new IOException("文件与子文件夹路径冲突");
                slash = path.IndexOf('/', slash + 1);
            }
        }
    }
}
