using LanFileHall;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        string root = Path.Combine(Path.GetTempPath(), "LanFileHall-UI-" + Guid.NewGuid().ToString("N"));
        var initial = new FileStore(root, Path.Combine(root, "downloads"));
        for (int i = 1; i <= 4; i++)
        {
            var path = Path.Combine(root, $"上次文件-{i}.txt");
            File.WriteAllText(path, "UI fixture only");
            initial.Publish([path]);
        }
        var restarted = new FileStore(root, Path.Combine(root, "downloads"));
        var folder = Path.Combine(root, "文件夹直接分享");
        Directory.CreateDirectory(Path.Combine(folder, "空目录"));
        File.WriteAllText(Path.Combine(folder, "中文.txt"), "folder fixture");
        restarted.Publish([folder]);
        for (int i = 1; i <= 6; i++)
        {
            var path = Path.Combine(root, $"本次文件-{i}.txt");
            File.WriteAllText(path, "UI fixture only");
            restarted.Publish([path]);
        }
        using var service = new HallService(restarted);
        service.Start(false);
        using var remote = new HallService(new FileStore(Path.Combine(root, "remote"), Path.Combine(root, "remote-downloads")));
        remote.Start(false);
        var localPeer = new Peer(service.Store.DeviceId, "测试电脑 A", "127.0.0.1", service.Port, DateTime.UtcNow);
        var fixture = new TextMessageRequest(Guid.NewGuid().ToString("N"), remote.Store.DeviceId, "测试电脑 B", remote.Port,
            service.Store.DeviceId, "中文文字消息 😃\r\n第二行，可直接复制。\r\n  保留空格  ");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var keepAlive = new System.Windows.Forms.Timer { Interval = 4000 };
        keepAlive.Tick += async (_, _) =>
        {
            using var body = new ByteArrayContent(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(fixture));
            try { using var response = await http.PostAsync($"http://127.0.0.1:{service.Port}/messages", body); }
            catch (HttpRequestException) { }
        };
        remote.MessagesChanged += () =>
        {
            var latest = remote.GetMessages().LastOrDefault();
            if (latest?.Incoming == true) _ = remote.SendTextMessage(localPeer, "测试回复：\r\n" + latest.Text, CancellationToken.None);
        };
        using var form = new MainForm(service);
        form.Shown += async (_, _) =>
        {
            using var body = new ByteArrayContent(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(fixture));
            using var response = await http.PostAsync($"http://127.0.0.1:{service.Port}/messages", body);
            keepAlive.Start();
        };
        Application.Run(form);
    }
}
