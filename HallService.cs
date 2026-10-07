using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LanFileHall;

public sealed partial class HallService : IDisposable
{
    public const int DiscoveryPort = 45831;
    private const string Protocol = "LAN-FILE-HALL/1";
    private readonly CancellationTokenSource stop = new();
    private readonly TcpListener listener = new(IPAddress.Any, 0);
    private readonly ConcurrentDictionary<string, Peer> peers = new();
    private readonly SemaphoreSlim connections = new(16);
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private UdpClient? discovery;
    private int discoveryPort = DiscoveryPort;
    public FileStore Store { get; }
    public int Port { get; private set; }
    public string? DiscoveryError { get; private set; }
    public event Action? Changed;

    public HallService(FileStore? store = null) { Store = store ?? new(); }

    public void Start(bool enableDiscovery = true, int discoveryPort = DiscoveryPort)
    {
        this.discoveryPort = discoveryPort;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoop();
        if (!enableDiscovery) return;
        try
        {
            discovery = new UdpClient(AddressFamily.InterNetwork);
            discovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            discovery.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
            discovery.EnableBroadcast = true;
            _ = ReceiveLoop();
            _ = BeaconLoop();
        }
        catch (SocketException ex) { DiscoveryError = ex.Message; }
    }

    public List<Peer> OnlinePeers() => peers.Values.Where(p => DateTime.UtcNow - p.SeenUtc < TimeSpan.FromSeconds(16)).OrderBy(p => p.Name).ToList();

    private async Task AcceptLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!connections.Wait(0)) { client.Dispose(); continue; }
                _ = Serve(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            try
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 8192)
                {
                    if (await stream.ReadAsync(one, deadline.Token) == 0) return;
                    header.Add(one[0]);
                    int n = header.Count;
                    if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
                }
                string headerText = Encoding.ASCII.GetString(header.ToArray());
                var first = headerText.Split("\r\n")[0].Split(' ');
                if (header.Count >= 8192 || first.Length != 3 || first[0] is not ("GET" or "POST"))
                { await Reply(stream, 400, "Bad request", deadline.Token); return; }
                if (first[0] == "POST")
                {
                    if (first[1] == "/messages") await ReceiveTextMessage(stream, headerText, (IPEndPoint)client.Client.RemoteEndPoint!, deadline.Token);
                    else await Reply(stream, 404, "Not found", deadline.Token);
                }
                else if (first[1] == "/catalog")
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(Store.GetCatalog());
                    await WriteHeader(stream, 200, bytes.Length, "application/json", deadline.Token);
                    await stream.WriteAsync(bytes, deadline.Token);
                }
                else if (await ServeFolderRequest(first[1], stream, deadline)) { }
                else if (first[1].StartsWith("/files/", StringComparison.Ordinal) && Guid.TryParseExact(first[1][7..], "N", out _))
                {
                    using var file = Store.OpenPublished(first[1][7..]);
                    if (file == null) { await Reply(stream, 404, "File unavailable", deadline.Token); return; }
                    await WriteHeader(stream, 200, file.Length, "application/octet-stream", deadline.Token);
                    var buffer = new byte[131072];
                    int count;
                    while ((count = await file.ReadAsync(buffer, deadline.Token)) > 0)
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(60));
                        await stream.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    }
                }
                else await Reply(stream, 404, "Not found", deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { connections.Release(); }
        }
    }

    private static Task WriteHeader(NetworkStream stream, int status, long length, string type, CancellationToken ct)
    {
        string reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request";
        return stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Length: {length}\r\nContent-Type: {type}\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\n\r\n"), ct).AsTask();
    }

    private static async Task Reply(NetworkStream stream, int status, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await WriteHeader(stream, status, bytes.Length, "text/plain", ct);
        await stream.WriteAsync(bytes, ct);
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var packet = await discovery!.ReceiveAsync(stop.Token);
                try
                {
                    if (packet.Buffer.Length > 2048) continue;
                    var beacon = JsonSerializer.Deserialize<Beacon>(packet.Buffer);
                    if (beacon == null || beacon.Protocol != Protocol || beacon.DeviceId == Store.DeviceId ||
                        !Guid.TryParseExact(beacon.DeviceId, "N", out _) || beacon.Port < 1 || beacon.Port > 65535 || string.IsNullOrWhiteSpace(beacon.DeviceName)) continue;
                    peers[beacon.DeviceId] = new(beacon.DeviceId, beacon.DeviceName[..Math.Min(80, beacon.DeviceName.Length)], packet.RemoteEndPoint.Address.ToString(), beacon.Port, DateTime.UtcNow);
                    Changed?.Invoke();
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var results = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        foreach (var address in adapter.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
        {
            var ip = address.Address.GetAddressBytes();
            var mask = address.IPv4Mask.GetAddressBytes();
            results.Add(new IPAddress(ip.Zip(mask, (a, b) => (byte)(a | ~b)).ToArray()));
        }
        return results;
    }

    private async Task BeaconLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Beacon(Protocol, Store.DeviceId, Environment.MachineName, Port));
                foreach (var address in BroadcastAddresses())
                {
                    try { await discovery!.SendAsync(bytes, new IPEndPoint(address, discoveryPort), stop.Token); }
                    catch (SocketException) { }
                }
                foreach (var entry in peers.Where(p => DateTime.UtcNow - p.Value.SeenUtc > TimeSpan.FromSeconds(16))) peers.TryRemove(entry.Key, out _);
                Changed?.Invoke();
                await Task.Delay(4000, stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or NetworkInformationException) { }
    }

    public async Task<Catalog> ReadCatalog(Peer peer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var response = await http.GetAsync($"http://{peer.Host}:{peer.Port}/catalog", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024).WaitAsync(timeout.Token);
        var result = JsonSerializer.Deserialize<Catalog>(await response.Content.ReadAsByteArrayAsync(timeout.Token)) ?? throw new IOException("文件列表为空");
        if (result.DeviceId != peer.Id || result.Files == null || result.Files.Any(f => f == null || !Guid.TryParseExact(f.Id, "N", out _) || string.IsNullOrWhiteSpace(f.Name) || f.Size < 0)) throw new IOException("无效的文件列表");
        return result;
    }

    public async Task<string> Download(Peer peer, FileOffer file, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        if (file.IsDirectory) return await DownloadFolder(peer, file, progress, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.GetAsync($"http://{peer.Host}:{peer.Port}/files/{file.Id}", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? throw new IOException("发送方未提供文件大小");
        if (total != file.Size) throw new IOException("文件已变化，请刷新列表后重新下载");
        string name = SafeName(file.Name);
        string temporary = Path.Combine(Store.DownloadDirectory, "." + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            {
                var buffer = new byte[131072];
                long done = 0;
                int count;
                var lastUpdate = DateTime.MinValue;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    done += count;
                    if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds >= 100 || done == total)
                    { progress?.Report((done, total)); lastUpdate = DateTime.UtcNow; }
                }
                if (done != total) throw new IOException("传输中断，文件不完整");
            }
            ct.ThrowIfCancellationRequested();
            for (int i = 0; ; i++)
            {
                var candidate = Path.Combine(Store.DownloadDirectory, i == 0 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
                try { File.Move(temporary, candidate); return candidate; }
                catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string SafeName(string input)
    {
        var name = Path.GetFileName(input);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length > 180) name = name[..160] + Path.GetExtension(name)[..Math.Min(20, Path.GetExtension(name).Length)];
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(name)) name = "下载文件";
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem)) name = "_" + name;
        return name;
    }

    public void Dispose()
    {
        stop.Cancel();
        discovery?.Dispose();
        listener.Stop();
        http.Dispose();
    }
}
