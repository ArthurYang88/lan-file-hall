using System.Diagnostics;

namespace LanFileHall;

public sealed class MainForm : Form
{
    private readonly HallService service;
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage filesPage = new("文件大厅");
    private readonly TabPage messagesPage = new("文字消息");
    private HashSet<string> seenMessages = [];
    private int unreadMessages;
    private readonly ListView list = new();
    private readonly ListViewGroup currentUploads = new("本次上传") { CollapsedState = ListViewGroupCollapsedState.Expanded };
    private readonly ListViewGroup previousUploads = new("上次上传") { CollapsedState = ListViewGroupCollapsedState.Collapsed };
    private readonly TextBox search = new();
    private readonly Label summary = new();
    private readonly Label status = new();
    private readonly ProgressBar progress = new();
    private readonly Button download;
    private readonly Button remove;
    private readonly Button cancel;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? transfer;
    private List<HallRow> rows = [];
    private bool refreshing;
    private bool mineOnly;
    private bool downloading;
    private bool closing;

    public MainForm(HallService service)
    {
        this.service = service;
        Text = "局域网文件大厅";
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(244, 247, 251);
        ForeColor = Color.FromArgb(28, 40, 59);
        Size = new Size(1080, 760);
        MinimumSize = new Size(900, 640);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 8 };
        foreach (int height in new[] { 52, 42, 56, 48, 34 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        Controls.Add(tabs);
        filesPage.BackColor = BackColor;
        messagesPage.BackColor = BackColor;
        filesPage.Controls.Add(layout);
        messagesPage.Controls.Add(new MessagePanel(service));
        tabs.TabPages.AddRange([filesPage, messagesPage]);
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab == messagesPage) { unreadMessages = 0; messagesPage.Text = "文字消息"; }
        };
        service.MessagesChanged += MessagesChanged;
        layout.Controls.Add(new Label { Text = "局域网文件大厅", Font = new Font(Font.FontFamily, 25, FontStyle.Bold), AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = "发布给同一网络里的电脑，需要时再下载。", AutoSize = true, ForeColor = Color.SlateGray, Margin = new Padding(0, 5, 0, 0) }, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(ActionButton("＋ 发布文件", Publish, true));
        actions.Controls.Add(ActionButton("＋ 发布文件夹", PublishFolder));
        actions.Controls.Add(ActionButton("打开下载文件夹", (_, _) => OpenDownloads()));
        actions.Controls.Add(ActionButton("刷新大厅", async (_, _) => await RefreshHall()));
        layout.Controls.Add(actions, 0, 2);

        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        search.PlaceholderText = "搜索文件名或电脑名称…";
        search.Dock = DockStyle.Fill;
        search.TextChanged += (_, _) => RenderRows();
        filters.Controls.Add(search, 0, 0);
        var mine = new CheckBox { Text = "只看我的发布", AutoSize = true, Margin = new Padding(16, 3, 0, 0) };
        mine.CheckedChanged += (_, _) => { mineOnly = mine.Checked; RenderRows(); };
        filters.Controls.Add(mine, 1, 0);
        layout.Controls.Add(filters, 0, 3);

        summary.Dock = DockStyle.Fill;
        summary.ForeColor = Color.SlateGray;
        layout.Controls.Add(summary, 0, 4);
        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.MultiSelect = true;
        list.Groups.AddRange([currentUploads, previousUploads]);
        list.HideSelection = false;
        list.BorderStyle = BorderStyle.None;
        list.Columns.Add("文件 / 文件夹", 390);
        list.Columns.Add("大小", 100);
        list.Columns.Add("来自电脑", 200);
        list.Columns.Add("修改时间", 180);
        list.SelectedIndexChanged += (_, _) => UpdateActions();
        list.GroupCollapsedStateChanged += (_, _) =>
        {
            foreach (var item in list.SelectedItems.Cast<ListViewItem>().ToArray())
                if (item.Group?.CollapsedState == ListViewGroupCollapsedState.Collapsed) item.Selected = false;
            UpdateActions();
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem item in list.Items)
                    item.Selected = item.Group?.CollapsedState != ListViewGroupCollapsedState.Collapsed;
                e.SuppressKeyPress = true;
            }
        };
        list.DoubleClick += async (_, _) => await DownloadSelected();
        layout.Controls.Add(list, 0, 5);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), WrapContents = false };
        download = ActionButton("下载选中文件", async (_, _) => await DownloadSelected(), true);
        remove = ActionButton("取消发布", async (_, _) =>
        {
            try
            {
                foreach (var row in SelectedRows().Where(r => r.Mine)) service.Store.Unpublish(row.File.Id);
                await RefreshHall();
            }
            catch (Exception ex) { MessageBox.Show(this, "取消发布失败：" + ex.Message); }
        });
        cancel = ActionButton("取消下载", (_, _) => transfer?.Cancel());
        bottom.Controls.AddRange([download, remove, cancel]);
        layout.Controls.Add(bottom, 0, 6);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        progress.Dock = DockStyle.Fill;
        progress.Margin = Padding.Empty;
        footer.Controls.Add(progress, 0, 0);
        status.Dock = DockStyle.Fill;
        status.Padding = new Padding(0, 8, 0, 0);
        status.Text = "将文件或文件夹拖到窗口即可发布。关闭 App 后停止共享，下次打开自动恢复。";
        status.ForeColor = Color.SlateGray;
        footer.Controls.Add(status, 0, 1);
        layout.Controls.Add(footer, 0, 7);

        EnableDrop(this);
        timer.Tick += async (_, _) => await RefreshHall();
        Shown += async (_, _) =>
        {
            if (service.DiscoveryError != null) status.Text = "自动发现未启动：" + service.DiscoveryError;
            await RefreshHall();
            timer.Start();
        };
        FormClosing += (_, e) =>
        {
            closing = true;
            timer.Stop();
            lifetime.Cancel();
            transfer?.Cancel();
            if (downloading) { e.Cancel = true; Hide(); }
        };
        FormClosed += (_, _) => { timer.Dispose(); service.MessagesChanged -= MessagesChanged; };
        UpdateActions();
    }

    private void MessagesChanged()
    {
        if (IsDisposed || !IsHandleCreated || lifetime.IsCancellationRequested) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed || lifetime.IsCancellationRequested) return;
                var incoming = service.GetMessages().Where(m => m.Incoming).Select(m => m.PeerId + ":" + m.Id).ToHashSet();
                unreadMessages += incoming.Except(seenMessages).Count();
                seenMessages = incoming;
                if (tabs.SelectedTab == messagesPage) unreadMessages = 0;
                messagesPage.Text = unreadMessages > 0 ? $"文字消息（{unreadMessages}）" : "文字消息";
            }));
        }
        catch (InvalidOperationException) { }
    }

    private Button ActionButton(string title, EventHandler handler, bool primary = false)
    {
        var button = new Button { Text = title, AutoSize = true, Height = 38, Padding = new Padding(12, 3, 12, 3), Margin = new Padding(0, 0, 12, 0), FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(44, 93, 224) : Color.White, ForeColor = primary ? Color.White : ForeColor, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0;
        button.Click += handler;
        return button;
    }

    private List<HallRow> SelectedRows() => list.SelectedItems.Cast<ListViewItem>()
        .Where(item => item.Group?.CollapsedState != ListViewGroupCollapsedState.Collapsed)
        .Select(item => (HallRow)item.Tag!).ToList();
    private void UpdateActions()
    {
        if (download == null) return;
        var selected = SelectedRows();
        int remoteCount = selected.Count(r => !r.Mine);
        int ownCount = selected.Count(r => r.Mine);
        download.Enabled = !downloading && remoteCount > 0;
        download.Text = remoteCount > 0 ? $"下载选中文件（{remoteCount}）" : "下载选中文件";
        remove.Enabled = ownCount > 0;
        remove.Text = ownCount > 0 ? $"取消发布（{ownCount}）" : "取消发布";
        cancel.Enabled = downloading;
    }

    private async void Publish(object? sender, EventArgs e)
    {
        using var picker = new OpenFileDialog { Multiselect = true, Title = "选择要发布的文件", CheckFileExists = true };
        if (picker.ShowDialog(this) == DialogResult.OK) await PublishPaths(picker.FileNames);
    }

    private async void PublishFolder(object? sender, EventArgs e)
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "选择要发布的文件夹（包含子文件夹）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (picker.ShowDialog(this) == DialogResult.OK) await PublishPaths([picker.SelectedPath]);
    }

    private void EnableDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        control.DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await PublishPaths(paths);
        };
        foreach (Control child in control.Controls) EnableDrop(child);
    }

    private async Task PublishPaths(string[] paths)
    {
        try
        {
            var available = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            service.Store.Publish(available);
            if (!downloading) status.Text = available.Length == paths.Length ? "已发布。其他在线电脑将在几秒内看到；原文件或文件夹需保留在当前位置。" : "已发布可读取的文件和文件夹；部分路径不存在，已跳过。";
            await RefreshHall();
        }
        catch (Exception ex) { MessageBox.Show(this, "发布失败：" + ex.Message); }
    }

    private async Task RefreshHall()
    {
        if (refreshing || lifetime.IsCancellationRequested) return;
        refreshing = true;
        try
        {
            var peers = service.OnlinePeers();
            var own = new Peer(service.Store.DeviceId, Environment.MachineName, "127.0.0.1", service.Port, DateTime.UtcNow);
            var next = service.Store.GetCatalog().Files.Select(f => new HallRow(own, f, true)).ToList();
            var remote = await Task.WhenAll(peers.Select(async peer =>
            {
                try { return (await service.ReadCatalog(peer, lifetime.Token)).Files.Select(f => new HallRow(peer, f, false)).ToList(); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException) { return new List<HallRow>(); }
            }));
            if (IsDisposed || lifetime.IsCancellationRequested) return;
            foreach (var batch in remote) next.AddRange(batch);
            rows = next;
            RenderRows();
        }
        finally { refreshing = false; }
    }

    private void RenderRows()
    {
        string query = search.Text.Trim();
        var visible = rows.Where(r => (!mineOnly || r.Mine) && (r.File.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Peer.Name.Contains(query, StringComparison.OrdinalIgnoreCase))).OrderBy(r => r.File.Name).ThenBy(r => r.Peer.Name).ToList();
        // Keep existing native items so periodic refresh preserves Ctrl selection,
        // the Shift anchor, keyboard focus, scrolling and group collapse state.
        static string Key(HallRow row) => row.Peer.Id + ":" + row.File.Id;
        var wanted = visible.Select(Key).ToHashSet();
        list.BeginUpdate();
        foreach (var item in list.Items.Cast<ListViewItem>().ToArray())
            if (!wanted.Contains(Key((HallRow)item.Tag!))) list.Items.Remove(item);
        var existing = list.Items.Cast<ListViewItem>().ToDictionary(item => Key((HallRow)item.Tag!));
        int position = 0;
        foreach (var row in visible)
        {
            if (!existing.TryGetValue(Key(row), out var item))
            {
                item = new ListViewItem([row.File.Name, "", "", ""]) { Tag = row };
                list.Items.Insert(Math.Min(position, list.Items.Count), item);
            }
            item.Tag = row;
            item.Text = row.File.Name;
            item.SubItems[1].Text = row.File.IsDirectory ? "文件夹" : FormatSize(row.File.Size);
            item.SubItems[2].Text = row.Peer.Name + (row.Mine ? "（本机）" : "");
            item.SubItems[3].Text = row.File.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var group = row.File.PreviousUpload ? previousUploads : currentUploads;
            if (item.Group != group) item.Group = group;
            position++;
        }
        currentUploads.Header = $"本次上传（{visible.Count(r => !r.File.PreviousUpload)}）";
        previousUploads.Header = $"上次上传（{visible.Count(r => r.File.PreviousUpload)}）";
        list.EndUpdate();
        int others = service.OnlinePeers().Count;
        summary.Text = $"{others + 1} 台在线电脑（含本机）   ·   {visible.Count} 个文件 / 文件夹" + (others == 0 ? "   ·   等待其他电脑打开 App；请允许防火墙的专用网络访问" : "");
        if (visible.Count == 0) summary.Text += rows.Count == 0 ? "   ·   点击“发布文件”开始" : "   ·   没有匹配的文件";
        UpdateActions();
    }

    private async Task DownloadSelected()
    {
        var queue = SelectedRows().Where(r => !r.Mine).ToList();
        if (downloading || queue.Count == 0) return;
        downloading = true;
        transfer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        UpdateActions();
        progress.Value = 0;
        int completed = 0;
        var failures = new List<string>();
        status.Text = $"准备下载 {queue.Count} 个文件…";
        try
        {
            for (int index = 0; index < queue.Count; index++)
            {
            transfer.Token.ThrowIfCancellationRequested();
            var row = queue[index];
            int ordinal = index + 1;
            bool active = true;
            var report = new Progress<(long Done, long Total)>(p =>
            {
                if (IsDisposed || closing || !active) return;
                progress.Value = p.Total == 0 ? 100 : (int)Math.Clamp(p.Done * 100.0 / p.Total, 0, 100);
                status.Text = $"[{ordinal}/{queue.Count}] 正在下载 {row.File.Name}   {FormatSize(p.Done)} / {FormatSize(p.Total)}";
            });
            try
            {
                progress.Value = 0;
                status.Text = $"[{ordinal}/{queue.Count}] 正在连接 {row.Peer.Name}…";
                await service.Download(row.Peer, row.File, report, transfer.Token);
                completed++;
            }
            catch (OperationCanceledException) when (transfer.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(row.File.Name + "：" + ex.Message); }
            finally { active = false; }
            }
            progress.Value = 100;
            status.Text = $"下载完成：{completed} 个成功，{failures.Count} 个失败。已保存到专用下载文件夹。";
            if (failures.Count > 0 && !closing) MessageBox.Show(this, string.Join(Environment.NewLine, failures), "部分文件下载失败");
        }
        catch (OperationCanceledException) { status.Text = $"下载队列已取消，已完成 {completed} 个文件；未完成的临时文件已清理。"; progress.Value = 0; }
        catch (Exception ex) { status.Text = "下载失败：" + ex.Message; progress.Value = 0; }
        finally
        {
            downloading = false;
            transfer.Dispose();
            transfer = null;
            UpdateActions();
            if (closing) Close();
        }
    }

    private void OpenDownloads()
    {
        try { Process.Start(new ProcessStartInfo(service.Store.DownloadDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }

    public static string FormatSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = size;
        int i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {units[i]}";
    }
}
