namespace GalDanmaku.Core.Text;

public enum DialogueEventKind { NewDialogue, TextUpdated }
public enum TextFilterReason { None, Empty, Duplicate, Invalid }
public sealed record DialogueEvent(Guid SessionId, long DialogueId, long Sequence, DateTimeOffset ReceivedAt,
    DialogueEventKind Kind, string Text);
public sealed record TextProcessResult(DialogueEvent? Event, TextFilterReason FilterReason);

public sealed class DialogueProcessor
{
    public const int MaximumTextLength = 65536;
    private readonly TimeSpan _mergeGap;
    private Guid _sessionId = Guid.NewGuid();
    private string? _previous;
    private DateTimeOffset _lastChange;
    private long _dialogueId;
    private long _sequence;
    private bool _canMerge;

    public DialogueProcessor(TimeSpan mergeGap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(mergeGap, TimeSpan.Zero);
        _mergeGap = mergeGap;
    }

    public TextProcessResult Process(string raw, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length > MaximumTextLength) return new(null, TextFilterReason.Invalid);
        string normalized;
        try { normalized = TextNormalizer.Normalize(raw); }
        catch (ArgumentException) { return new(null, TextFilterReason.Invalid); }
        if (normalized.Length == 0) return new(null, TextFilterReason.Empty);
        if (normalized == _previous) return new(null, TextFilterReason.Duplicate);

        TimeSpan elapsed = receivedAt - _lastChange;
        bool update = _canMerge && _previous is not null && normalized.StartsWith(_previous, StringComparison.Ordinal)
            && elapsed >= TimeSpan.Zero && elapsed <= _mergeGap;
        if (!update) _dialogueId++;
        _sequence++;
        _previous = normalized;
        _lastChange = receivedAt;
        _canMerge = true;
        return new(new(_sessionId, _dialogueId, _sequence, receivedAt,
            update ? DialogueEventKind.TextUpdated : DialogueEventKind.NewDialogue, normalized), TextFilterReason.None);
    }

    // A reconnect can repeat the last frame; preserve deduplication, but never merge across an unknown gap.
    public void OnDisconnected() => _canMerge = false;

    // After a reconnect the consumer's story context was reset, so a repeated frame that Process filtered as
    // a duplicate is the only evidence of the current line. Republishes it as a new dialogue; null if nothing was seen.
    public DialogueEvent? Reacquire(DateTimeOffset receivedAt)
    {
        if (_previous is null) return null;
        _dialogueId++;
        _sequence++;
        _lastChange = receivedAt;
        _canMerge = true;
        return new(_sessionId, _dialogueId, _sequence, receivedAt, DialogueEventKind.NewDialogue, _previous);
    }

    public void Reset()
    {
        _sessionId = Guid.NewGuid();
        _previous = null;
        _dialogueId = _sequence = 0;
        _canMerge = false;
    }

}
