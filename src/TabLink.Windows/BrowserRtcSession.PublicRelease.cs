#if TABLINK_NO_BROWSER
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using TabLink.Core;

namespace TabLink.Windows;

// GitHub's globally downloadable public binary omits SIPSorcery because its
// current upstream license contains a geographic distribution restriction.
// The normal source build keeps the complete browser receiver.
internal sealed class BrowserRtcSession : IAsyncDisposable
{
    readonly WebSocket socket;
    readonly Action<BrowserSessionStatus> report;

    internal static bool IsSupported => false;
    public Guid Id { get; }
    public int MediaPort { get; }

    internal BrowserRtcSession(Guid id, IPAddress address, int mediaPort, WebSocket socket,
        TabletDisplayProfile profile,
        Func<Guid, TabletDisplayProfile, CancellationToken, Task<BrowserDisplaySession>> prepare,
        Action<BrowserSessionStatus> report, Action presented, Action<string> diagnostic,
        CancellationToken ct)
    {
        Id = id;
        MediaPort = mediaPort;
        this.socket = socket;
        this.report = report;
    }

    internal Task RunAsync()
    {
        report(new(Id, "closed", "此公开发行包未包含浏览器 WebRTC 接收组件。请使用 Android 原生客户端，或从源码自行构建适用版本。"));
        return Task.CompletedTask;
    }

    internal static async Task<JsonDocument> ReadJsonAsync(WebSocket socket, int maximum, CancellationToken ct)
    {
        var bytes = new byte[maximum];
        var offset = 0;
        while (true)
        {
            if (offset == maximum) throw new InvalidDataException("信令过大。");
            var result = await socket.ReceiveAsync(bytes.AsMemory(offset), ct);
            if (result.MessageType == WebSocketMessageType.Close) throw new EndOfStreamException("浏览器已断开。");
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("需要文本信令。");
            offset += result.Count;
            if (result.EndOfMessage)
                return JsonDocument.Parse(bytes.AsMemory(0, offset), new JsonDocumentOptions { MaxDepth = 16 });
        }
    }

    public ValueTask DisposeAsync()
    {
        try { socket.Abort(); } catch { }
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
#endif
