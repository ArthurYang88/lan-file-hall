using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace LanFileHall;

public record TextMessageRequest(string Id, string SenderId, string SenderName, int SenderPort, string RecipientId, string Text);
public record TextMessageReceipt(string Id, string RecipientId);
public enum MessageState { Received, Sending, Delivered, Unconfirmed }
public record TextMessage(string Id, string PeerId, string PeerName, string Text, DateTime TimeUtc, bool Incoming, MessageState State);

public sealed partial class HallService
{
    public const int MaxMessageCharacters = 32000;
    private const int MaxMessageBytes = 256 * 1024;
    private readonly object messageGate = new();
    private readonly List<TextMessage> messages = [];
    public event Action? MessagesChanged;

    public List<TextMessage> GetMessages()
    {
        lock (messageGate) return messages.ToList();
    }

    private void AddMessage(TextMessage message)
    {
        lock (messageGate)
        {
            messages.Add(message);
            if (messages.Count > 500) messages.RemoveAt(0);
        }
        MessagesChanged?.Invoke();
    }

    private void SetMessageState(string id, MessageState state)
    {
        lock (messageGate)
        {
            int index = messages.FindIndex(m => m.Id == id && !m.Incoming);
            if (index >= 0) messages[index] = messages[index] with { State = state };
        }
        MessagesChanged?.Invoke();
    }

    private async Task ReceiveTextMessage(NetworkStream stream, string header, IPEndPoint remote, CancellationToken ct)
    {
        var headers = header.Split("\r\n").Skip(1).Where(line => line.Contains(':')).Select(line =>
        {
            int colon = line.IndexOf(':');
            return (Name: line[..colon].Trim(), Value: line[(colon + 1)..].Trim());
        }).ToList();
        var lengths = headers.Where(h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).ToList();
        if (lengths.Count != 1 || !int.TryParse(lengths[0].Value, out int length) || length <= 0 || length > MaxMessageBytes ||
            headers.Any(h => h.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
        { await Reply(stream, 400, "Invalid message length", ct); return; }
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct);
        TextMessageRequest? request;
        try { request = JsonSerializer.Deserialize<TextMessageRequest>(bytes); }
        catch (JsonException) { await Reply(stream, 400, "Invalid message", ct); return; }
        if (request == null || !Guid.TryParseExact(request.Id, "N", out _) || !Guid.TryParseExact(request.SenderId, "N", out _) ||
            request.RecipientId != Store.DeviceId || request.SenderPort < 1 || request.SenderPort > 65535 ||
            string.IsNullOrWhiteSpace(request.SenderName) || request.SenderName.Length > 80 ||
            string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > MaxMessageCharacters || request.Text.Contains('\0'))
        { await Reply(stream, 400, "Invalid message", ct); return; }

        bool added;
        lock (messageGate)
        {
            added = !messages.Any(m => m.Incoming && m.Id == request.Id && m.PeerId == request.SenderId);
            if (added)
            {
                messages.Add(new(request.Id, request.SenderId, request.SenderName, request.Text, DateTime.UtcNow, true, MessageState.Received));
                if (messages.Count > 500) messages.RemoveAt(0);
            }
        }
        if (request.SenderId != Store.DeviceId)
            peers[request.SenderId] = new(request.SenderId, request.SenderName, remote.Address.ToString(), request.SenderPort, DateTime.UtcNow);
        if (added) MessagesChanged?.Invoke();
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new TextMessageReceipt(request.Id, Store.DeviceId));
        await WriteHeader(stream, 200, receipt.Length, "application/json", ct);
        await stream.WriteAsync(receipt, ct);
    }

    public async Task SendTextMessage(Peer peer, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("请输入要发送的文字。" );
        if (text.Length > MaxMessageCharacters || text.Contains('\0')) throw new ArgumentException("每条消息最多 32,000 个字符。" );
        ct.ThrowIfCancellationRequested();
        string id = Guid.NewGuid().ToString("N");
        var request = new TextMessageRequest(id, Store.DeviceId, Environment.MachineName, Port, peer.Id, text);
        AddMessage(new(id, peer.Id, peer.Name, text, DateTime.UtcNow, false, MessageState.Sending));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var body = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request));
            body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var outgoing = new HttpRequestMessage(HttpMethod.Post, $"http://{peer.Host}:{peer.Port}/messages") { Content = body };
            using var response = await http.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new IOException("对方版本不支持文字消息，请双方更新到 v1.3 或更新版本。" );
            if (response.StatusCode == HttpStatusCode.BadRequest)
                throw new IOException("对方无法接收此消息，请确认双方均已更新到 v1.3 或更新版本。" );
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(4096).WaitAsync(timeout.Token);
            var receipt = JsonSerializer.Deserialize<TextMessageReceipt>(await response.Content.ReadAsByteArrayAsync(timeout.Token));
            if (receipt == null || receipt.Id != id || receipt.RecipientId != peer.Id) throw new IOException("未收到有效的接收确认。" );
            SetMessageState(id, MessageState.Delivered);
        }
        catch
        {
            SetMessageState(id, MessageState.Unconfirmed);
            throw;
        }
    }
}
