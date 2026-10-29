using System.Net.WebSockets;
using System.Text;

namespace GalDanmaku.Core.Text;

public sealed class LunaTextSource : ITextSource
{
    public const int MaximumMessageBytes = 65536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly TextSourceOptions _options;
    private readonly Uri _endpoint;

    public LunaTextSource(TextSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _endpoint = new Uri(options.Endpoint);
    }

    public async Task RunAsync(Func<TextSourceEvent, CancellationToken, ValueTask> onEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        int retryDelay = _options.InitialRetryMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await onEvent(new TextConnectionChanged(TextConnectionState.Connecting, "正在连接 LunaTranslator 原文服务…"), cancellationToken).ConfigureAwait(false);
            string? failure;
            using (var socket = new ClientWebSocket())
            {
                // Luna's origin server rejects unsolicited Pong, which the BCL otherwise sends every 30 seconds.
                socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
                if (_endpoint.IsLoopback) socket.Options.Proxy = null;
                failure = await ConnectAsync(socket, cancellationToken).ConfigureAwait(false);
                if (failure is null)
                {
                    await onEvent(new TextConnectionChanged(TextConnectionState.Connected, "已连接，等待游戏原文。"), cancellationToken).ConfigureAwait(false);
                    while (failure is null)
                    {
                        (string? text, failure) = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
                        if (text is not null)
                        {
                            retryDelay = _options.InitialRetryMilliseconds;
                            cancellationToken.ThrowIfCancellationRequested();
                            await onEvent(new TextFrame(text, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            await onEvent(new TextConnectionChanged(TextConnectionState.Retrying, $"{failure} {retryDelay} 毫秒后重试。"), cancellationToken).ConfigureAwait(false);
            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            retryDelay = Math.Min(retryDelay * 2, _options.MaximumRetryMilliseconds);
        }
    }

    private async Task<string?> ConnectAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConnectTimeoutMilliseconds);
        try
        {
            await socket.ConnectAsync(_endpoint, timeout.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return "连接超时，请检查服务状态与地址。"; }
        catch (WebSocketException) when (!cancellationToken.IsCancellationRequested)
        { return "连接失败，请确认 LunaTranslator 已开启网络服务及原文输出，且地址和端口正确。"; }
    }

    private static async Task<(string? Text, string? Failure)> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return (null, "原文服务已关闭连接。");
                if (result.MessageType != WebSocketMessageType.Text) return (null, "已拒绝二进制消息；原文接口应发送 UTF-8 文本。");
                if (message.Length + result.Count > MaximumMessageBytes) return (null, "已拒绝超过 64 KiB 的消息。");
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return (StrictUtf8.GetString(message.GetBuffer(), 0, (int)message.Length), null);
            }
        }
        catch (DecoderFallbackException) { return (null, "已拒绝无效 UTF-8 消息。"); }
        catch (WebSocketException) when (!cancellationToken.IsCancellationRequested)
        { return (null, "连接中断或收到无效 WebSocket 消息。"); }
        catch (IOException) when (!cancellationToken.IsCancellationRequested)
        { return (null, "读取原文时连接中断。"); }
    }
}
