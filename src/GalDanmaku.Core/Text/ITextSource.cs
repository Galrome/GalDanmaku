namespace GalDanmaku.Core.Text;

public enum TextConnectionState { Connecting, Connected, Retrying, Completed }
public abstract record TextSourceEvent;
public sealed record TextFrame(string Text, DateTimeOffset ReceivedAt) : TextSourceEvent;
public sealed record TextConnectionChanged(TextConnectionState State, string Detail) : TextSourceEvent;

public interface ITextSource
{
    // The source awaits each callback; consumers provide backpressure without an unbounded queue.
    Task RunAsync(Func<TextSourceEvent, CancellationToken, ValueTask> onEvent, CancellationToken cancellationToken);
}
