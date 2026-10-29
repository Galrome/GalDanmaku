namespace GalDanmaku.Core.Text;

public sealed class MockTextSource : ITextSource
{
    private static readonly string[] Demo = ["", "今", "今日", "今日", "今日は", "今日は晴れ。", "今日は晴れ。", "次のページへ。", "次のページへ。"];
    private readonly TimeSpan _interval;
    private readonly string[] _script;

    // A non-empty script replaces the built-in demo frames; null or empty keeps the demo.
    public MockTextSource(TimeSpan? interval = null, IReadOnlyList<string>? script = null)
    {
        if (script is { Count: > 0 })
        {
            if (script.Any(frame => frame is null)) throw new ArgumentException("脚本帧不能为 null。", nameof(script));
            _script = script.ToArray();
        }
        else _script = Demo;
        _interval = interval ?? TimeSpan.FromMilliseconds(400);
        ArgumentOutOfRangeException.ThrowIfLessThan(_interval, TimeSpan.Zero);
    }

    public async Task RunAsync(Func<TextSourceEvent, CancellationToken, ValueTask> onEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        cancellationToken.ThrowIfCancellationRequested();
        await onEvent(new TextConnectionChanged(TextConnectionState.Connected, "模拟演示运行中：逐字、重复与下一句。"), cancellationToken).ConfigureAwait(false);
        foreach (string text in _script)
        {
            await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await onEvent(new TextFrame(text, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await onEvent(new TextConnectionChanged(TextConnectionState.Completed, "模拟演示已完成，可再次开始。"), cancellationToken).ConfigureAwait(false);
    }
}
