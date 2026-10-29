using System.Net.WebSockets;
using System.Text;
using GalDanmaku.Core.Text;

namespace GalDanmaku.Tests;

public sealed class LunaTextSourceTests
{
    private static TextSourceOptions Options(LoopbackWebSocketServer server) => new()
    {
        Endpoint = server.Endpoint,
        InitialRetryMilliseconds = 50,
        MaximumRetryMilliseconds = 100,
        ConnectTimeoutMilliseconds = 300
    };

    [Fact]
    public async Task ReassemblesUtf8FragmentsAndReceivesLiteralJsonAsPlainText()
    {
        using var server = new LoopbackWebSocketServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var frames = new List<string>();
        Task run = new LunaTextSource(Options(server)).RunAsync((item, _) =>
        {
            if (item is TextFrame frame) { frames.Add(frame.Text); if (frames.Count == 2) stop.Cancel(); }
            return ValueTask.CompletedTask;
        }, stop.Token);
        Assert.False(run.IsCompleted, "A subscribed source must remain active until stopped.");
        Task serve = ServeAsync();
        await IgnoreCancellationAsync(run);
        await serve;
        Assert.Equal(["日本語👩‍👩‍👧‍👦", "{\"text\":\"本物の台詞\"}"], frames);

        async Task ServeAsync()
        {
            var connection = await server.AcceptAsync(deadline.Token);
            using var client = connection.Client;
            using var socket = connection.Socket;
            byte[] bytes = Encoding.UTF8.GetBytes("日本語👩‍👩‍👧‍👦");
            await socket.SendAsync(bytes.AsMemory(0, 1), WebSocketMessageType.Text, false, deadline.Token);
            await socket.SendAsync(bytes.AsMemory(1, 3), WebSocketMessageType.Text, false, deadline.Token);
            await socket.SendAsync(bytes.AsMemory(4), WebSocketMessageType.Text, true, deadline.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"text\":\"本物の台詞\"}").AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
            Assert.False(client.GetStream().DataAvailable); // No application subscription payload.
        }
    }

    [Fact]
    public async Task RemoteCloseReconnectsAndReceivesNextConnection()
    {
        using var server = new LoopbackWebSocketServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var statuses = new List<TextConnectionState>();
        var frames = new List<string>();
        Task run = new LunaTextSource(Options(server)).RunAsync((item, _) =>
        {
            if (item is TextConnectionChanged status) statuses.Add(status.State);
            if (item is TextFrame frame) { frames.Add(frame.Text); if (frames.Count == 2) stop.Cancel(); }
            return ValueTask.CompletedTask;
        }, stop.Token);
        Task serve = ServeAsync();
        await IgnoreCancellationAsync(run);
        await serve;
        Assert.Equal(["第一句", "第二句"], frames);
        Assert.Contains(TextConnectionState.Retrying, statuses);
        Assert.Equal(2, statuses.Count(s => s == TextConnectionState.Connected));

        async Task ServeAsync()
        {
            foreach (string text in new[] { "第一句", "第二句" })
            {
                var connection = await server.AcceptAsync(deadline.Token);
                using var client = connection.Client;
                using var socket = connection.Socket;
                await socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", deadline.Token);
            }
        }
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("oversized")]
    [InlineData("malformed")]
    public async Task RejectsInvalidMessagesWithoutPublishingPartialText(string kind)
    {
        using var server = new LoopbackWebSocketServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        int frames = 0;
        string? failure = null;
        Task run = new LunaTextSource(Options(server)).RunAsync((item, _) =>
        {
            if (item is TextFrame) frames++;
            if (item is TextConnectionChanged { State: TextConnectionState.Retrying } status)
            { failure = status.Detail; stop.Cancel(); }
            return ValueTask.CompletedTask;
        }, stop.Token);
        Task serve = ServeAsync();
        await IgnoreCancellationAsync(run);
        await serve;
        Assert.Equal(0, frames);
        Assert.False(string.IsNullOrEmpty(failure));

        async Task ServeAsync()
        {
            var connection = await server.AcceptAsync(deadline.Token);
            using var client = connection.Client;
            using var socket = connection.Socket;
            if (kind == "malformed")
            {
                // An invalid UTF-8 text frame, authored directly under RFC 6455.
                await client.GetStream().WriteAsync(new byte[] { 0x81, 0x02, 0xC3, 0x28 }, deadline.Token);
            }
            else
            {
                byte[] bytes = kind == "oversized" ? new byte[LunaTextSource.MaximumMessageBytes + 1] : [1, 2, 3];
                await socket.SendAsync(bytes.AsMemory(), kind == "binary" ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, deadline.Token);
            }
            await IgnoreCancellationAsync(Task.Delay(Timeout.Infinite, stop.Token));
        }
    }

    [Fact]
    public async Task RefusedConnectionRecoversWhenServerStartsDuringRetry()
    {
        using var server = new LoopbackWebSocketServer(start: false);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int retries = 0;
        string? received = null;
        Task run = new LunaTextSource(Options(server)).RunAsync((item, _) =>
        {
            if (item is TextConnectionChanged { State: TextConnectionState.Retrying } && ++retries == 3)
            { server.Start(); started.TrySetResult(); }
            if (item is TextFrame frame) { received = frame.Text; stop.Cancel(); }
            return ValueTask.CompletedTask;
        }, stop.Token);
        try
        {
            await started.Task.WaitAsync(stop.Token);
            var connection = await server.AcceptAsync(stop.Token);
            using var client = connection.Client;
            using var socket = connection.Socket;
            await socket.SendAsync("再接続。"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, stop.Token);
            await IgnoreCancellationAsync(run);
            Assert.Equal(3, retries);
            Assert.Equal("再接続。", received);
        }
        finally { stop.Cancel(); await IgnoreCancellationAsync(run); }
    }

    [Fact]
    public async Task HandshakeTimeoutReportsRetry()
    {
        using var server = new LoopbackWebSocketServer();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool retried = false;
        Task run = new LunaTextSource(Options(server)).RunAsync((item, _) =>
        {
            if (item is TextConnectionChanged { State: TextConnectionState.Retrying }) { retried = true; stop.Cancel(); }
            return ValueTask.CompletedTask;
        }, stop.Token);
        using var client = await server.AcceptWithoutHandshakeAsync(stop.Token);
        await IgnoreCancellationAsync(run);
        Assert.True(retried);
    }

    internal static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { /* Cancellation is the expected end of an infinite source. */ }
    }
}
