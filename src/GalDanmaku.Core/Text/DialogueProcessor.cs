using GalDanmaku.Core.Diagnostics;

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

    public Guid SessionId => _sessionId;

    public TextProcessResult Process(string raw, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(raw);
        using var scope = RuntimeLog.BeginScope(new LogContext(TextSessionId: _sessionId.ToString(), DialogueId: _dialogueId));
        RuntimeLog.Write(LogLevel.Debug, "DialogueProcessor", "ProcessingStarted", new
        {
            receivedAt, length = raw.Length, previousLength = _previous?.Length,
            elapsedMilliseconds = _previous is null ? (double?)null : (receivedAt - _lastChange).TotalMilliseconds,
            mergeGapMilliseconds = _mergeGap.TotalMilliseconds, canMerge = _canMerge,
            eventSequence = _sequence
        });
        if (raw.Length > MaximumTextLength)
        {
            RuntimeLog.Write(LogLevel.Debug, "DialogueProcessor", "FilteredInvalid", new { reason = "MaximumTextLength", length = raw.Length, maximum = MaximumTextLength });
            return new(null, TextFilterReason.Invalid);
        }
        string normalized;
        try { normalized = TextNormalizer.Normalize(raw); }
        catch (ArgumentException exception)
        {
            RuntimeLog.Write(LogLevel.Warning, "DialogueProcessor", "FilteredInvalid", new { reason = "NormalizationRejected", length = raw.Length }, exception);
            return new(null, TextFilterReason.Invalid);
        }
        RuntimeLog.Write(LogLevel.Debug, "DialogueProcessor", "TextNormalized", new { inputLength = raw.Length, normalizedLength = normalized.Length, normalized = RuntimeLog.Text(normalized) });
        if (normalized.Length == 0)
        {
            RuntimeLog.Write(LogLevel.Debug, "DialogueProcessor", "FilteredEmpty", new { reason = "EmptyAfterNormalization" });
            return new(null, TextFilterReason.Empty);
        }
        if (normalized == _previous)
        {
            RuntimeLog.Write(LogLevel.Debug, "DialogueProcessor", "FilteredDuplicate", new { reason = "SameNormalizedText", canMerge = _canMerge, prefix = true, elapsedMilliseconds = (receivedAt - _lastChange).TotalMilliseconds, mergeGapMilliseconds = _mergeGap.TotalMilliseconds, eventSequence = _sequence });
            return new(null, TextFilterReason.Duplicate);
        }

        TimeSpan elapsed = receivedAt - _lastChange;
        bool prefix = _previous is not null && normalized.StartsWith(_previous, StringComparison.Ordinal);
        bool canMerge = _canMerge;
        bool update = _canMerge && _previous is not null && normalized.StartsWith(_previous, StringComparison.Ordinal)
            && elapsed >= TimeSpan.Zero && elapsed <= _mergeGap;
        if (!update) _dialogueId++;
        _sequence++;
        _previous = normalized;
        _lastChange = receivedAt;
        _canMerge = true;
        using var resultScope = RuntimeLog.BeginScope(new LogContext(DialogueId: _dialogueId));
        RuntimeLog.Write(LogLevel.Debug, "DialogueProcessor", update ? "TextUpdated" : "NewDialogue", new
        {
            eventSequence = _sequence, receivedAt, elapsedMilliseconds = elapsed.TotalMilliseconds,
            mergeGapMilliseconds = _mergeGap.TotalMilliseconds, prefix, canMerge,
            reason = update ? "PrefixWithinMergeGap" : !canMerge ? "MergeUnavailable" : !prefix ? "NotPrefix" : elapsed < TimeSpan.Zero ? "TimestampReversed" : "MergeGapExceeded",
            length = normalized.Length, normalized = RuntimeLog.Text(normalized)
        });
        return new(new(_sessionId, _dialogueId, _sequence, receivedAt,
            update ? DialogueEventKind.TextUpdated : DialogueEventKind.NewDialogue, normalized), TextFilterReason.None);
    }

    // A reconnect can repeat the last frame; preserve deduplication, but never merge across an unknown gap.
    public void OnDisconnected()
    {
        RuntimeLog.Write(LogLevel.Debug, "DialogueProcessor", "MergeDisabled", new { reason = "SourceDisconnected", eventSequence = _sequence });
        _canMerge = false;
    }

    // After a reconnect the consumer's story context was reset, so a repeated frame that Process filtered as
    // a duplicate is the only evidence of the current line. Republishes it as a new dialogue; null if nothing was seen.
    public DialogueEvent? Reacquire(DateTimeOffset receivedAt)
    {
        if (_previous is null)
        {
            RuntimeLog.Write(LogLevel.Debug, "DialogueProcessor", "ReacquireSkipped", new { reason = "NoPreviousDialogue" });
            return null;
        }
        _dialogueId++;
        _sequence++;
        _lastChange = receivedAt;
        _canMerge = true;
        using var scope = RuntimeLog.BeginScope(new LogContext(TextSessionId: _sessionId.ToString(), DialogueId: _dialogueId));
        RuntimeLog.Write(LogLevel.Debug, "DialogueProcessor", "DialogueReacquired", new { reason = "DuplicateAfterReconnectContextReset", eventSequence = _sequence, receivedAt, length = _previous.Length, normalized = RuntimeLog.Text(_previous) });
        return new(_sessionId, _dialogueId, _sequence, receivedAt, DialogueEventKind.NewDialogue, _previous);
    }

    public void Reset()
    {
        RuntimeLog.Write(LogLevel.Debug, "DialogueProcessor", "ProcessorReset", new { previousSessionId = _sessionId, previousDialogueId = _dialogueId, previousEventSequence = _sequence });
        _sessionId = Guid.NewGuid();
        _previous = null;
        _dialogueId = _sequence = 0;
        _canMerge = false;
    }

}
