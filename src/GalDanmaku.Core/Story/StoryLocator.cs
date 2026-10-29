using GalDanmaku.Core.Text;

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
        if (_sessionId != dialogueEvent.SessionId)
        {
            if (_retiredSessions.Contains(dialogueEvent.SessionId)) return Current;
            if (_sessionId is Guid previousSession) _retiredSessions.Add(previousSession);
            ResetContext();
            _sessionId = dialogueEvent.SessionId;
        }
        if (dialogueEvent.Sequence <= _sequence || dialogueEvent.DialogueId < _dialogueId) return Current;
        _sequence = dialogueEvent.Sequence;
        bool sameDialogue = dialogueEvent.DialogueId == _dialogueId;
        if (sameDialogue && dialogueEvent.Kind == DialogueEventKind.NewDialogue) return Current;
        _dialogueId = dialogueEvent.DialogueId;

        string text;
        try
        {
            text = dialogueEvent.Text is null || dialogueEvent.Text.Length > DialogueProcessor.MaximumTextLength
                ? string.Empty : TextNormalizer.Normalize(dialogueEvent.Text);
        }
        catch (ArgumentException) { text = string.Empty; }

        if (sameDialogue && _history.Count > 0 && _history[^1].Text == text) return Current;
        IReadOnlyList<StoryNode> globalCandidates = _index.FindPrefix(text);
        if (globalCandidates.Count == 0)
        {
            _history.Clear();
            _confirmedAt = null;
            return Current = Unknown("当前文本不在索引中；已清除路线上下文。");
        }

        var observation = new Observation(text, globalCandidates);
        if (sameDialogue && _history.Count > 0) _history[^1] = observation;
        else _history.Add(observation);
        if (_history.Count > MaximumHistoryCount) _history.RemoveAt(0);

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
        return Current = next;
    }

    public void Reset()
    {
        _retiredSessions.Clear();
        ResetContext();
    }

    private void ResetContext()
    {
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

    private static StoryLocation Unknown(string reason) => new(StoryLocationStatus.Unknown, null, [], reason);
    private sealed record Observation(string Text, IReadOnlyList<StoryNode> Candidates);
}
