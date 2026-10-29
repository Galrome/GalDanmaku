using GalDanmaku.Core.Text;
using GalDanmaku.Core.Diagnostics;

namespace GalDanmaku.Core.Story;

public sealed class StoryLocator
{
    public const int MaximumHistoryCount = 6;
    private readonly StoryIndex _index;
    private readonly TimeProvider _timeProvider;
    private readonly List<Observation> _history = [];
    private readonly HashSet<Guid> _retiredSessions = [];
    private Guid? _sessionId;
    private long _dialogueId;
    private long _sequence;
    private long? _confirmedAt;

    public StoryLocator(StoryIndex index, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public StoryLocation Current { get; private set; } = Unknown("等待文本。");
    public int HistoryCount => _history.Count;
    // Increments whenever a node is (re)confirmed; never rewinds, even across Reset.
    public long ConfirmationVersion { get; private set; }
    public long? OffsetMilliseconds => _confirmedAt is long started
        ? Math.Max(0, (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds)
        : null;

    public StoryLocation Process(DialogueEvent dialogueEvent)
    {
        ArgumentNullException.ThrowIfNull(dialogueEvent);
        using var scope = RuntimeLog.BeginScope(new LogContext(TextSessionId: dialogueEvent.SessionId.ToString(), DialogueId: dialogueEvent.DialogueId, Sequence: dialogueEvent.Sequence));
        RuntimeLog.Write(LogLevel.Debug, "StoryLocator", "DialogueReceived", new { kind = dialogueEvent.Kind.ToString(), eventSequence = dialogueEvent.Sequence, receivedAt = dialogueEvent.ReceivedAt, historyCount = _history.Count, length = dialogueEvent.Text?.Length });
        if (_sessionId != dialogueEvent.SessionId)
        {
            if (_retiredSessions.Contains(dialogueEvent.SessionId))
            {
                RuntimeLog.Write(LogLevel.Debug, "StoryLocator", "LocatorSkipped", new { reason = "RetiredSession", historyCount = _history.Count });
                return Current;
            }
            if (_sessionId is Guid previousSession) _retiredSessions.Add(previousSession);
            ResetContext("SessionChanged");
            _sessionId = dialogueEvent.SessionId;
        }
        if (dialogueEvent.Sequence <= _sequence || dialogueEvent.DialogueId < _dialogueId)
        {
            RuntimeLog.Write(LogLevel.Debug, "StoryLocator", "LocatorSkipped", new { reason = dialogueEvent.Sequence <= _sequence ? "StaleSequence" : "OlderDialogue", lastEventSequence = _sequence, lastDialogueId = _dialogueId, historyCount = _history.Count });
            return Current;
        }
        _sequence = dialogueEvent.Sequence;
        bool sameDialogue = dialogueEvent.DialogueId == _dialogueId;
        if (sameDialogue && dialogueEvent.Kind == DialogueEventKind.NewDialogue)
        {
            RuntimeLog.Write(LogLevel.Debug, "StoryLocator", "LocatorSkipped", new { reason = "RepeatedNewDialogue", historyCount = _history.Count });
            return Current;
        }
        _dialogueId = dialogueEvent.DialogueId;

        string text;
        try
        {
            text = dialogueEvent.Text is null || dialogueEvent.Text.Length > DialogueProcessor.MaximumTextLength
                ? string.Empty : TextNormalizer.Normalize(dialogueEvent.Text);
        }
        catch (ArgumentException exception)
        {
            RuntimeLog.Write(LogLevel.Warning, "StoryLocator", "InvalidDialogueText", new { reason = "NormalizationRejected" }, exception);
            text = string.Empty;
        }

        if (sameDialogue && _history.Count > 0 && _history[^1].Text == text)
        {
            RuntimeLog.Write(LogLevel.Debug, "StoryLocator", "LocatorSkipped", new { reason = "SameDialogueText", historyCount = _history.Count });
            return Current;
        }
        IReadOnlyList<StoryNode> globalCandidates = _index.FindPrefix(text);
        RuntimeLog.Write(LogLevel.Debug, "StoryLocator", "CandidatesFound", new { candidateCount = globalCandidates.Count, candidateIds = RuntimeLog.DetailedText ? globalCandidates.Select(node => node.Id).ToArray() : null, length = text.Length, normalized = RuntimeLog.Text(text) });
        if (globalCandidates.Count == 0)
        {
            RuntimeLog.Write(LogLevel.Debug, "StoryLocator", "HistoryReset", new { reason = "TextOutsideIndex", previousHistoryCount = _history.Count });
            _history.Clear();
            _confirmedAt = null;
            return ReportLocation(Unknown("当前文本不在索引中；已清除路线上下文。"), "TextOutsideIndex");
        }

        var observation = new Observation(text, globalCandidates);
        if (sameDialogue && _history.Count > 0) _history[^1] = observation;
        else _history.Add(observation);
        if (_history.Count > MaximumHistoryCount)
        {
            _history.RemoveAt(0);
            RuntimeLog.Write(LogLevel.Debug, "StoryLocator", "HistoryTrimmed", new { reason = "MaximumHistoryCount", historyCount = _history.Count });
        }

        IReadOnlyList<StoryNode> candidates = MatchHistory();
        StoryLocation next;
        if (candidates.Count == 1 && candidates[0].NormalizedText == text)
        {
            StoryNode node = candidates[0];
            if (!sameDialogue || Current.Node?.Id != node.Id || _confirmedAt is null)
            {
                _confirmedAt = _timeProvider.GetTimestamp();
                ConfirmationVersion++;
            }
            next = new(StoryLocationStatus.Confirmed, node, candidates, "已通过完整文本及近期上下文确认节点。");
        }
        else
        {
            _confirmedAt = null;
            bool incomplete = candidates.Any(node => node.NormalizedText != text);
            next = new(StoryLocationStatus.Ambiguous, null, candidates,
                incomplete ? "文本可能仍在逐字输出，等待完整文本。" : "存在多个剧情节点，等待更多上下文。");
        }
        return ReportLocation(next, next.Status == StoryLocationStatus.Confirmed ? "FullTextAndContext" : candidates.Any(node => node.NormalizedText != text) ? "IncompleteText" : "MultipleCandidates");
    }

    public void Reset()
    {
        _retiredSessions.Clear();
        ResetContext("ExplicitReset");
    }

    private void ResetContext(string reason)
    {
        RuntimeLog.Write(LogLevel.Debug, "StoryLocator", "HistoryReset", new { reason, previousHistoryCount = _history.Count, previousSessionId = _sessionId, previousDialogueId = _dialogueId, previousEventSequence = _sequence });
        _history.Clear();
        _sessionId = null;
        _dialogueId = _sequence = 0;
        _confirmedAt = null;
        Current = Unknown("等待文本；路线上下文已重置。");
    }

    private IReadOnlyList<StoryNode> MatchHistory()
    {
        IReadOnlyList<StoryNode> candidates = _history[0].Candidates;
        for (int i = 1; i < _history.Count; i++)
        {
            HashSet<string> reachable = ReachableAfterOneObservation(candidates);
            StoryNode[] connected = _history[i].Candidates.Where(node => reachable.Contains(node.Id)).ToArray();
            if (connected.Length > 0) candidates = Array.AsReadOnly(connected);
            else
            {
                // A save load or missing section can invalidate every old path. Reacquire from this text.
                // Keep observations so a correction of the current dialogue can reconsider its predecessor.
                RuntimeLog.Write(LogLevel.Debug, "StoryLocator", "ContextDisconnected", new { reason = "NoReachableCandidate", historyPosition = i, historyCount = _history.Count, previousCandidateCount = candidates.Count, currentCandidateCount = _history[i].Candidates.Count });
                candidates = _history[i].Candidates;
            }
        }
        return candidates;
    }

    private HashSet<string> ReachableAfterOneObservation(IReadOnlyList<StoryNode> previous)
    {
        var direct = new HashSet<string>(StringComparer.Ordinal);
        foreach (StoryNode node in previous) direct.UnionWith(node.NextIds);
        var reachable = new HashSet<string>(direct, StringComparer.Ordinal);
        // Permit one missed node, but never rank a direct path above an equally possible skipped path.
        foreach (string id in direct) reachable.UnionWith(_index.Nodes[id].NextIds);
        return reachable;
    }

    private StoryLocation ReportLocation(StoryLocation next, string reason)
    {
        bool changed = Current.Status != next.Status || Current.Node?.Id != next.Node?.Id;
        using var scope = RuntimeLog.BeginScope(new LogContext(NodeId: next.Node?.Id));
        RuntimeLog.Write(changed ? LogLevel.Information : LogLevel.Debug, "StoryLocator", "LocationEvaluated", new
        {
            previousStatus = Current.Status.ToString(), status = next.Status.ToString(), reason,
            nodeId = next.Node?.Id, candidateCount = next.Candidates.Count,
            candidateIds = RuntimeLog.DetailedText ? next.Candidates.Select(node => node.Id).ToArray() : null,
            historyCount = _history.Count, confirmationVersion = ConfirmationVersion, stateChanged = changed
        });
        return Current = next;
    }

    private static StoryLocation Unknown(string reason) => new(StoryLocationStatus.Unknown, null, [], reason);
    private sealed record Observation(string Text, IReadOnlyList<StoryNode> Candidates);
}
