using System.Runtime.InteropServices;

namespace LanFileHall;

public sealed class MessagePanel : UserControl
{
    private readonly HallService service;
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly ComboBox recipient = new() { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(Peer.Name), Dock = DockStyle.Fill };
    private readonly ListView history = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill };
    private readonly TextBox detail = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly TextBox compose = new() { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, MaxLength = HallService.MaxMessageCharacters, Dock = DockStyle.Fill, PlaceholderText = "输入文字，支持多行；Ctrl+Enter 发送" };
    private readonly Button send = new() { Text = "发送", AutoSize = true, Padding = new Padding(18, 3, 18, 3) };
    private readonly Button copy = new() { Text = "复制消息", AutoSize = true, Enabled = false, Padding = new Padding(12, 3, 12, 3) };
    private readonly Label feedback = new() { AutoSize = true, ForeColor = Color.SlateGray };
    private readonly Label count = new() { AutoSize = true, ForeColor = Color.SlateGray };
    private bool sending;
    private bool rendering;

    public MessagePanel(HallService service)
    {
        this.service = service;
        Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "文字消息", Font = new Font("Microsoft YaHei UI", 23, FontStyle.Bold), AutoSize = true }, 0, 0);
        count.Text = "收到和发出的文字都可以复制；每条最多 32,000 字符，当前会话保留最近 500 条。";
        layout.Controls.Add(count, 0, 1);
        history.Columns.Add("对方电脑", 180);
        history.Columns.Add("状态", 100);
        history.Columns.Add("时间", 150);
        history.Columns.Add("文字预览", 450);
        history.SelectedIndexChanged += (_, _) => ShowSelectedMessage();
        layout.Controls.Add(history, 0, 2);
        var detailHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        detailHeader.Controls.Add(new Label { Text = "选中消息查看全文", AutoSize = true, Margin = new Padding(0, 5, 16, 0) });
        detailHeader.Controls.Add(copy);
        copy.Click += (_, _) => CopySelectedMessage();
        layout.Controls.Add(detailHeader, 0, 3);
        detail.AccessibleName = "消息全文，可选择复制";
        layout.Controls.Add(detail, 0, 4);
        var recipientRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 8, 0, 0) };
        recipientRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        recipientRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        recipientRow.Controls.Add(new Label { Text = "发送给", AutoSize = true }, 0, 0);
        recipient.AccessibleName = "接收电脑";
        recipient.SelectedIndexChanged += (_, _) => UpdateSendButton();
        recipientRow.Controls.Add(recipient, 1, 0);
        layout.Controls.Add(recipientRow, 0, 5);
        compose.AccessibleName = "要发送的文字";
        compose.TextChanged += (_, _) => UpdateSendButton();
        compose.KeyDown += async (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await SendMessage();
            }
        };
        layout.Controls.Add(compose, 0, 6);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 8, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        send.Click += async (_, _) => await SendMessage();
        footer.Controls.Add(send, 0, 0);
        feedback.AutoSize = false;
        feedback.AutoEllipsis = true;
        feedback.Dock = DockStyle.Fill;
        feedback.Margin = new Padding(12, 5, 0, 0);
        footer.Controls.Add(feedback, 1, 0);
        layout.Controls.Add(footer, 0, 7);
        service.MessagesChanged += MessagesChanged;
        timer.Tick += (_, _) => RefreshPeers();
        Load += (_, _) => { RefreshPeers(); RefreshMessages(); timer.Start(); };
        feedback.Text = "等待其他电脑上线。";
        UpdateSendButton();
    }

    private void MessagesChanged()
    {
        if (IsDisposed || !IsHandleCreated || lifetime.IsCancellationRequested) return;
        try { BeginInvoke((Action)RefreshMessages); }
        catch (InvalidOperationException) { }
    }

    private void RefreshPeers()
    {
        if (recipient.DroppedDown || sending) return;
        string? selected = (recipient.SelectedItem as Peer)?.Id;
        var online = service.OnlinePeers();
        var previous = recipient.Items.Cast<Peer>().ToList();
        if (previous.Count == online.Count && previous.Zip(online).All(pair => pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name && pair.First.Host == pair.Second.Host && pair.First.Port == pair.Second.Port))
            return;
        recipient.BeginUpdate();
        recipient.Items.Clear();
        foreach (var peer in online) recipient.Items.Add(peer);
        recipient.SelectedIndex = online.FindIndex(p => p.Id == selected);
        // A draft must never silently switch to a different computer when its
        // recipient goes offline.
        if (recipient.SelectedIndex < 0 && online.Count > 0 && string.IsNullOrWhiteSpace(compose.Text)) recipient.SelectedIndex = 0;
        recipient.EndUpdate();
        feedback.Text = online.Count == 0 ? "等待其他电脑上线。" : recipient.SelectedIndex < 0 ? "请选择接收电脑，输入文字已保留。" : "";
        UpdateSendButton();
    }

    private TextMessage? SelectedMessage() => history.SelectedItems.Count > 0 ? history.SelectedItems[0].Tag as TextMessage : null;

    private void RefreshMessages()
    {
        if (IsDisposed || lifetime.IsCancellationRequested) return;
        var messages = service.GetMessages();
        var selected = SelectedMessage();
        var existing = history.Items.Cast<ListViewItem>().ToDictionary(i => MessageKey((TextMessage)i.Tag!));
        var wanted = messages.Select(MessageKey).ToHashSet();
        rendering = true;
        history.BeginUpdate();
        foreach (var item in history.Items.Cast<ListViewItem>().ToArray())
            if (!wanted.Contains(MessageKey((TextMessage)item.Tag!))) history.Items.Remove(item);
        ListViewItem? lastAdded = null;
        foreach (var message in messages)
        {
            if (!existing.TryGetValue(MessageKey(message), out var item))
            {
                item = new ListViewItem([message.PeerName, "", "", ""]) { Tag = message };
                history.Items.Add(item);
                lastAdded = item;
            }
            item.Tag = message;
            item.SubItems[1].Text = StateName(message);
            item.SubItems[2].Text = message.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
            string preview = message.Text.Replace("\r", " ").Replace("\n", " ");
            item.SubItems[3].Text = preview.Length > 100 ? preview[..100] + "…" : preview;
        }
        if (selected == null && lastAdded != null) { lastAdded.Selected = true; lastAdded.EnsureVisible(); }
        history.EndUpdate();
        rendering = false;
        ShowSelectedMessage();
        RefreshPeers();
    }

    private static string MessageKey(TextMessage message) => message.PeerId + ":" + message.Id + ":" + message.Incoming;
    private static string StateName(TextMessage message) => message.State switch
    {
        MessageState.Received => "收到",
        MessageState.Sending => "发送中",
        MessageState.Delivered => "已送达",
        _ => "未确认送达"
    };

    private void ShowSelectedMessage()
    {
        if (rendering) return;
        var selected = SelectedMessage();
        string text = selected?.Text ?? "";
        if (detail.Text != text) detail.Text = text;
        copy.Enabled = selected != null;
    }

    private void CopySelectedMessage()
    {
        if (SelectedMessage() is not { } selected) return;
        try { Clipboard.SetText(selected.Text); feedback.Text = "已复制整条消息。"; }
        catch (ExternalException) { feedback.Text = "剪贴板正在被其他程序使用，请重试。"; }
    }

    private void UpdateSendButton() => send.Enabled = !sending && recipient.SelectedItem is Peer && !string.IsNullOrWhiteSpace(compose.Text);

    private async Task SendMessage()
    {
        if (sending || recipient.SelectedItem is not Peer peer || string.IsNullOrWhiteSpace(compose.Text)) return;
        string text = compose.Text;
        sending = true;
        compose.ReadOnly = true;
        recipient.Enabled = false;
        UpdateSendButton();
        feedback.Text = "正在发送…";
        try
        {
            await service.SendTextMessage(peer, text, lifetime.Token);
            if (!IsDisposed) { compose.Clear(); feedback.Text = "已送达 " + peer.Name + "。"; }
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !lifetime.IsCancellationRequested)
                feedback.Text = ex is OperationCanceledException ? "未确认送达：连接超时，文字已保留。" : "未确认送达：" + ex.Message;
        }
        finally
        {
            sending = false;
            if (!IsDisposed)
            {
                compose.ReadOnly = false;
                recipient.Enabled = true;
                UpdateSendButton();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lifetime.Cancel();
            service.MessagesChanged -= MessagesChanged;
            timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
