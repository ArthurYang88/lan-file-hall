namespace LanFileHall;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            Environment.ExitCode = SelfTest.RunAsync().GetAwaiter().GetResult();
            return;
        }
        using var mutex = new Mutex(true, "Local\\LanFileHall.Desktop", out bool first);
        if (!first)
        {
            MessageBox.Show("文件大厅已经打开，请查看任务栏。", "局域网文件大厅");
            return;
        }
        ApplicationConfiguration.Initialize();
        try
        {
            using var service = new HallService();
            service.Start();
            Application.Run(new MainForm(service));
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动失败：" + ex.Message, "局域网文件大厅", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
