using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace LanFileHall;

internal static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        var report = new List<string>();
        string root = Path.Combine(Path.GetTempPath(), "LanFileHall-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "中文 sample.bin");
            var bytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 7);
            await File.WriteAllBytesAsync(source, bytes);
            var aStore = new FileStore(Path.Combine(root, "a"), Path.Combine(root, "a-downloads"));
            aStore.Publish([source, source]);
            Check(aStore.GetCatalog().Files.Count == 1, "duplicate publish ignored");
            Check(!aStore.GetCatalog().Files.Single().PreviousUpload, "new publication belongs to current session");
            var reloaded = new FileStore(Path.Combine(root, "a"), Path.Combine(root, "a-downloads"));
            Check(reloaded.GetCatalog().Files.Count == 1 && reloaded.DeviceId == aStore.DeviceId, "published files and identity persist");
            Check(reloaded.GetCatalog().Files.Single().PreviousUpload, "previous publications move to history after restart");
            string fresh = Path.Combine(root, "fresh.txt");
            await File.WriteAllTextAsync(fresh, "new session");
            reloaded.Publish([fresh]);
            Check(reloaded.GetCatalog().Files.Single(f => f.Name == "fresh.txt").PreviousUpload == false,
                "publishing after restart keeps history separate");
            Check(JsonSerializer.Deserialize<FileOffer>("{\"Id\":\"legacy\",\"Name\":\"old-client.txt\",\"Size\":0,\"ModifiedUtc\":\"2026-01-01T00:00:00Z\"}")?.PreviousUpload == false,
                "legacy catalogs remain compatible");
            using var a = new HallService(aStore);
            using var b = new HallService(new FileStore(Path.Combine(root, "b"), Path.Combine(root, "b-downloads")));
            a.Start(false);
            using var portProbe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int testDiscoveryPort = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;
            portProbe.Close();
            b.Start(true, testDiscoveryPort);
            var peer = new Peer(aStore.DeviceId, "test-a", "127.0.0.1", a.Port, DateTime.UtcNow);
            using var sender = new UdpClient();
            var beacon = JsonSerializer.SerializeToUtf8Bytes(new Beacon("LAN-FILE-HALL/1", peer.Id, peer.Name, peer.Port));
            await sender.SendAsync(beacon, new IPEndPoint(IPAddress.Loopback, testDiscoveryPort));
            for (int i = 0; i < 20 && b.OnlinePeers().Count == 0; i++) await Task.Delay(50);
            Check(b.OnlinePeers().Any(p => p.Id == peer.Id), "UDP discovery receives peer");
            var catalog = await b.ReadCatalog(peer, CancellationToken.None);
            Check(catalog.Files.Count == 1, "catalog fetched over HTTP");
            Check(!JsonSerializer.Serialize(catalog).Contains(root.Replace("\\", "\\\\")), "catalog does not expose source path");
            var downloaded = await b.Download(peer, catalog.Files[0], null, CancellationToken.None);
            Check(SHA256.HashData(await File.ReadAllBytesAsync(downloaded)).SequenceEqual(SHA256.HashData(bytes)), "3 MB transfer SHA-256 matches");
            var second = await b.Download(peer, catalog.Files[0], null, CancellationToken.None);
            Check(second != downloaded && File.Exists(downloaded), "duplicate download preserves existing file");
            Check(b.Store.GetCatalog().Files.Count == 0, "downloaded files are not automatically published");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await b.Download(peer, catalog.Files[0], null, canceled.Token); throw new Exception("cancel ignored"); }
            catch (OperationCanceledException) { report.Add("PASS canceled download"); }
            Check(!Directory.GetFiles(b.Store.DownloadDirectory, "*.partial").Any(), "no partial files remain");
            await File.WriteAllTextAsync(source, "changed");
            try { await b.Download(peer, catalog.Files[0], null, CancellationToken.None); throw new Exception("stale size accepted"); }
            catch (IOException) { report.Add("PASS changed file rejected"); }
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            var forbidden = await http.GetAsync($"http://127.0.0.1:{a.Port}/files/{Guid.NewGuid():N}");
            Check(forbidden.StatusCode == HttpStatusCode.NotFound, "unpublished file unavailable");
            var traversal = await http.GetAsync($"http://127.0.0.1:{a.Port}/files/%2e%2e%2fstate.json");
            Check(traversal.StatusCode == HttpStatusCode.NotFound, "path traversal rejected");
            aStore.Unpublish(catalog.Files[0].Id);
            Check((await b.ReadCatalog(peer, CancellationToken.None)).Files.Count == 0 && File.Exists(source), "unpublish preserves original file");
            string empty = Path.Combine(root, "empty.txt");
            await File.WriteAllBytesAsync(empty, []);
            aStore.Publish([empty]);
            var emptyFile = (await b.ReadCatalog(peer, CancellationToken.None)).Files.Single();
            var emptyPath = await b.Download(peer, emptyFile, null, CancellationToken.None);
            Check(new FileInfo(emptyPath).Length == 0, "zero byte file transfers");
            await VerifyFolderTransfer(aStore, a, b, peer, root, bytes, report);
            await VerifyTextMessages(a, b, peer, report);
            report.Add("ALL TESTS PASSED");
            return 0;
        }
        catch (Exception ex) { report.Add("FAIL " + ex); return 1; }
        finally
        {
            await File.WriteAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "self-test-results.txt"), report);
            // This unique test directory is retained for inspection; no recursive deletion.
        }

        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception(label);
            report.Add("PASS " + label);
        }
    }

    private static async Task VerifyFolderTransfer(FileStore sourceStore, HallService sourceService, HallService receiver,
        Peer peer, string root, byte[] bytes, List<string> report)
    {
        string source = Path.Combine(root, "共享资料.v1");
        Directory.CreateDirectory(Path.Combine(source, "嵌套", "空目录"));
        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), bytes);
        await File.WriteAllTextAsync(Path.Combine(source, "嵌套", "中文 # & %.txt"), "folder transfer 中文");
        await File.WriteAllBytesAsync(Path.Combine(source, "嵌套", "zero.txt"), []);
        sourceStore.Publish([source, source + Path.DirectorySeparatorChar]);
        var catalog = await receiver.ReadCatalog(peer, CancellationToken.None);
        var folder = catalog.Files.Single(f => f.IsDirectory);
        Check(!folder.PreviousUpload, "new folder belongs to current session");
        var manifest = sourceStore.GetFolderManifest(folder.Id, CancellationToken.None)!;
        Check(manifest.Files.Count == 3 && manifest.Directories.Contains("嵌套/空目录"), "folder manifest preserves empty directories");
        Check(!JsonSerializer.Serialize(manifest).Contains(root.Replace("\\", "\\\\")), "folder manifest contains relative paths only");
        string downloaded = await receiver.Download(peer, folder, null, CancellationToken.None);
        Check(Directory.Exists(Path.Combine(downloaded, "嵌套", "空目录")), "empty nested directory restored");
        Check((await File.ReadAllTextAsync(Path.Combine(downloaded, "嵌套", "中文 # & %.txt"))) == "folder transfer 中文", "Unicode and URL special characters preserved");
        Check(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(downloaded, "payload.bin"))).SequenceEqual(SHA256.HashData(bytes)), "folder binary SHA-256 matches");
        Check(new FileInfo(Path.Combine(downloaded, "嵌套", "zero.txt")).Length == 0, "zero byte file inside folder restored");
        Check(File.GetLastWriteTimeUtc(Path.Combine(downloaded, "payload.bin")) == File.GetLastWriteTimeUtc(Path.Combine(source, "payload.bin")), "file modification time preserved");
        string duplicate = await receiver.Download(peer, folder, null, CancellationToken.None);
        Check(Path.GetFileName(duplicate) == "共享资料.v1 (1)" && Directory.Exists(downloaded), "duplicate folder keeps complete name and preserves previous download");
        Check(receiver.Store.GetCatalog().Files.Count == 0, "downloaded folder is not automatically published");

        using (var cancel = new CancellationTokenSource())
        {
            var callback = new CallbackProgress(p => { if (p.Done > 0) cancel.Cancel(); });
            try { await receiver.Download(peer, folder, callback, cancel.Token); throw new Exception("folder cancellation ignored"); }
            catch (OperationCanceledException) { report.Add("PASS folder canceled during transfer"); }
        }
        Check(!Directory.EnumerateFileSystemEntries(receiver.Store.DownloadDirectory).Any(p => p.EndsWith(".partial")), "folder cancellation removes all staged content");
        var lastFile = manifest.Files.Last();
        bool changed = false;
        var changeCallback = new CallbackProgress(p =>
        {
            if (p.Done <= 0 || changed) return;
            changed = true;
            File.SetLastWriteTimeUtc(FolderPaths.Resolve(source, lastFile.RelativePath), lastFile.ModifiedUtc.AddMinutes(1));
        });
        try { await receiver.Download(peer, folder, changeCallback, CancellationToken.None); throw new Exception("changed folder file accepted"); }
        catch (IOException) { report.Add("PASS folder file modified during transfer rejected"); }
        Check(!Directory.EnumerateFileSystemEntries(receiver.Store.DownloadDirectory).Any(p => p.EndsWith(".partial")), "folder failure removes staged content");

        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        foreach (string invalid in new[] { "../fresh.txt", "嵌套/../../fresh.txt", "C:/outside.txt", "嵌套\\zero.txt", "payload.bin:stream" })
        {
            var forbidden = await http.GetAsync($"http://127.0.0.1:{sourceService.Port}/folders/{folder.Id}/files?path={Uri.EscapeDataString(invalid)}");
            Check(forbidden.StatusCode == HttpStatusCode.NotFound, "folder path rejected: " + invalid);
        }
        var unpublished = await http.GetAsync($"http://127.0.0.1:{sourceService.Port}/folders/{Guid.NewGuid():N}");
        Check(unpublished.StatusCode == HttpStatusCode.NotFound, "unpublished folder unavailable");
        foreach (var malformed in new[]
        {
            new FolderManifest(folder.Id, ["../escape"], []),
            new FolderManifest(folder.Id, [], [new("same.txt", 1, DateTime.UtcNow), new("SAME.txt", 1, DateTime.UtcNow)]),
            new FolderManifest(folder.Id, ["child"], [new("child", 1, DateTime.UtcNow)]),
            new FolderManifest(folder.Id, [], [new("parent", 1, DateTime.UtcNow), new("parent/file.txt", 1, DateTime.UtcNow)])
        })
        {
            try { HallService.ValidateManifest(malformed, folder.Id); throw new Exception("malformed manifest accepted"); }
            catch (IOException) { report.Add("PASS malformed folder manifest rejected"); }
        }
        var restarted = new FileStore(Path.Combine(root, "a"), Path.Combine(root, "a-downloads"));
        Check(restarted.GetCatalog().Files.Single(f => f.Id == folder.Id).PreviousUpload, "folder moves to previous uploads after restart");
        string emptyFolder = Path.Combine(root, "完全空文件夹");
        Directory.CreateDirectory(emptyFolder);
        sourceStore.Publish([emptyFolder]);
        var emptyOffer = (await receiver.ReadCatalog(peer, CancellationToken.None)).Files.Single(f => f.Name == "完全空文件夹");
        string emptyDestination = await receiver.Download(peer, emptyOffer, null, CancellationToken.None);
        Check(Directory.Exists(emptyDestination) && !Directory.EnumerateFileSystemEntries(emptyDestination).Any(), "completely empty folder transfers");
        sourceStore.Unpublish(folder.Id);
        var removed = await http.GetAsync($"http://127.0.0.1:{sourceService.Port}/folders/{folder.Id}");
        Check(removed.StatusCode == HttpStatusCode.NotFound && Directory.Exists(source), "unpublish folder preserves source and removes remote access");

        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception(label);
            report.Add("PASS " + label);
        }
    }

    private sealed class CallbackProgress(Action<(long Done, long Total)> callback) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => callback(value);
    }

    private static async Task VerifyTextMessages(HallService a, HallService b, Peer peerA, List<string> report)
    {
        var peerB = new Peer(b.Store.DeviceId, "test-b", "127.0.0.1", b.Port, DateTime.UtcNow);
        string original = "  中文消息 😃\r\n第二行\n\t保留空格 <script>text only</script>  ";
        await a.SendTextMessage(peerB, original, CancellationToken.None);
        Check(a.GetMessages().Single().State == MessageState.Delivered, "sender receives delivery confirmation");
        Check(b.GetMessages().Single().Text == original && b.GetMessages().Single().Incoming, "Unicode multiline text arrives unchanged");
        Check(a.GetMessages().Single().Text == original, "sender keeps exact copy of sent text");
        await b.SendTextMessage(peerA, "双方都能回复", CancellationToken.None);
        Check(a.GetMessages().Last().Text == "双方都能回复" && a.GetMessages().Last().Incoming, "bidirectional messages delivered");
        string maximum = new('中', HallService.MaxMessageCharacters);
        await a.SendTextMessage(peerB, maximum, CancellationToken.None);
        Check(b.GetMessages().Last().Text == maximum, "maximum length Chinese message delivered");
        int before = a.GetMessages().Count;
        foreach (string invalid in new[] { " \r\n ", new string('x', HallService.MaxMessageCharacters + 1), "bad\0text" })
        {
            try { await a.SendTextMessage(peerB, invalid, CancellationToken.None); throw new Exception("invalid text accepted"); }
            catch (ArgumentException) { report.Add("PASS invalid outbound message rejected"); }
        }
        Check(a.GetMessages().Count == before, "invalid outbound text does not create history entries");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var duplicate = new TextMessageRequest(Guid.NewGuid().ToString("N"), a.Store.DeviceId, "test-a", a.Port, b.Store.DeviceId, "重复请求只显示一次");
        int incomingBefore = b.GetMessages().Count;
        for (int i = 0; i < 2; i++)
        {
            using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(duplicate));
            var response = await http.PostAsync($"http://127.0.0.1:{b.Port}/messages", content);
            Check(response.IsSuccessStatusCode, "repeated message request acknowledged");
        }
        Check(b.GetMessages().Count == incomingBefore + 1, "duplicate incoming message appears once");
        using (var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(duplicate with { RecipientId = Guid.NewGuid().ToString("N") })))
        {
            var response = await http.PostAsync($"http://127.0.0.1:{b.Port}/messages", content);
            Check(response.StatusCode == HttpStatusCode.BadRequest, "wrong recipient rejected");
        }
        using (var content = new StringContent("not-json"))
        {
            var response = await http.PostAsync($"http://127.0.0.1:{b.Port}/messages", content);
            Check(response.StatusCode == HttpStatusCode.BadRequest, "malformed text message rejected");
        }
        Check(b.GetMessages().Count == incomingBefore + 1, "invalid inbound requests do not create messages");
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, b.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("POST /messages HTTP/1.1\r\nHost: localhost\r\nContent-Length: 999999999\r\n\r\n"));
            var reply = new byte[1024];
            int count = await stream.ReadAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Check(System.Text.Encoding.ASCII.GetString(reply, 0, count).StartsWith("HTTP/1.1 400"), "oversized message rejected before body allocation");
        }
        var noHistory = await http.GetAsync($"http://127.0.0.1:{b.Port}/messages");
        Check(noHistory.StatusCode == HttpStatusCode.NotFound, "message history is not exposed over network");
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int offlinePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        try
        {
            await a.SendTextMessage(peerB with { Port = offlinePort }, "offline", CancellationToken.None);
            throw new Exception("offline message considered delivered");
        }
        catch (HttpRequestException) { report.Add("PASS offline send reports failure"); }
        Check(a.GetMessages().Last().State == MessageState.Unconfirmed, "failed send is never marked delivered");

        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception(label);
            report.Add("PASS " + label);
        }
    }
}
