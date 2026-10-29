using System.Diagnostics;
using GalDanmaku.Core.Diagnostics;

namespace GalDanmaku.Core;

public sealed class NodePlayback
{
    public const int MaximumPendingComments = 128;
    // A comment blocked for longer than this (node time) is dropped instead of retried forever.
    public const double MaximumPendingMilliseconds = 3000;
    // Due comments that cannot even enter a full pending queue are dropped once this late.
    public const double MaximumLatenessMilliseconds = 5000;
    private const double MaximumElapsedMilliseconds = long.MaxValue - 1024d;
    private readonly Dictionary<Guid, StoredDanmaku> _comments = [];
    private readonly Dictionary<string, DanmakuDisplay> _displays = [];
    private readonly PriorityQueue<DanmakuDisplay, (long Offset, long Created, Guid Id)> _scheduled = new();
    private readonly Queue<(DanmakuDisplay Comment, double EnqueuedMilliseconds)> _pending = new();
    private readonly HashSet<string> _emitted = [];
    private string _gameId = "";
    private string _nodeId = "";
    private double _elapsedMilliseconds;
    private bool _aggregationEnabled;
    private bool _isPlaying;
    private long _queueFullDrops;
    private long _pendingExpiredDrops;
    private long _blockedEmissions;
    private long _lastSchedulingLog = Stopwatch.GetTimestamp();

    // Switching presentation deliberately replays the node; saved records remain untouched.
    public bool AggregationEnabled
    {
        get => _aggregationEnabled;
        set
        {
            if (_aggregationEnabled == value) return;
            _aggregationEnabled = value;
            Replay();
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            using var scope = RuntimeLog.BeginScope(new LogContext(GameId: _gameId, NodeId: _nodeId));
            RuntimeLog.Write(LogLevel.Information, "Playback", value ? "PlaybackStarted" : "PlaybackPaused", new { offsetMs = OffsetMilliseconds });
        }
    }
    public long OffsetMilliseconds => (long)_elapsedMilliseconds;
    public string GameId => _gameId;
    public string NodeId => _nodeId;
    public int PendingCount => _pending.Count;
    public int SavedCount => _comments.Count;
    // Comments dropped as expired since the last Load/Replay/Clear; they stay saved and return on replay.
    public long ExpiredCount { get; private set; }
    public long ResetVersion { get; private set; }
    public long DisplayVersion { get; private set; }
    public IReadOnlyCollection<DanmakuDisplay> Displays => _displays.Values;
    public bool IsCurrentDisplay(DanmakuDisplay display) =>
        _displays.TryGetValue(display.DisplayId, out DanmakuDisplay? current) && ReferenceEquals(display, current);

    public void Load(string gameId, string nodeId, IEnumerable<StoredDanmaku> comments)
    {
        StoredDanmaku.ValidateIdentity(gameId, nameof(gameId));
        StoredDanmaku.ValidateIdentity(nodeId, nameof(nodeId));
        ArgumentNullException.ThrowIfNull(comments);
        StoredDanmaku[] loaded = comments.ToArray();
        if (loaded.Any(comment => comment.GameId != gameId || comment.NodeId != nodeId))
        {
            throw new ArgumentException("弹幕不属于当前游戏与节点。", nameof(comments));
        }

        LogSchedulingSummary(force: true);
        _gameId = gameId;
        _nodeId = nodeId;
        _comments.Clear();
        _displays.Clear();
        _emitted.Clear();
        foreach (StoredDanmaku comment in loaded)
        {
            _comments.TryAdd(comment.Id, comment);
        }

        Replay();
        using var scope = RuntimeLog.BeginScope(new LogContext(GameId: _gameId, NodeId: _nodeId));
        RuntimeLog.Write(LogLevel.Information, "Playback", "PlaybackLoaded", new { count = _comments.Count, displayCount = _displays.Count });
    }

    public void Clear()
    {
        LogSchedulingSummary(force: true);
        using var scope = RuntimeLog.BeginScope(new LogContext(GameId: _gameId, NodeId: _nodeId));
        RuntimeLog.Write(LogLevel.Debug, "Playback", "PlaybackCleared", new { count = _comments.Count, pendingCount = _pending.Count, expiredCount = ExpiredCount });
        IsPlaying = false;
        _gameId = "";
        _nodeId = "";
        _comments.Clear();
        _displays.Clear();
        _emitted.Clear();
        _elapsedMilliseconds = 0;
        _pending.Clear();
        _scheduled.Clear();
        ExpiredCount = 0;
        ResetVersion++;
        DisplayVersion++;
    }

    public void Advance(double seconds, Func<StoredDanmaku, bool> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        AdvanceDisplay(seconds, display => emit(display.Representative));
    }

    public void AdvanceDisplay(double seconds, Func<DanmakuDisplay, bool> emit)
    {
        DanmakuEngine.ValidateNonNegativeFinite(seconds, nameof(seconds));
        ArgumentNullException.ThrowIfNull(emit);
        if (!IsPlaying)
        {
            return;
        }

        _elapsedMilliseconds = Math.Min(_elapsedMilliseconds + seconds * 1000, MaximumElapsedMilliseconds);
        while (_scheduled.TryPeek(out DanmakuDisplay? comment, out var priority) && priority.Offset <= _elapsedMilliseconds)
        {
            if (_pending.Count < MaximumPendingComments)
            {
                _pending.Enqueue((_scheduled.Dequeue(), _elapsedMilliseconds));
            }
            else if (_elapsedMilliseconds - priority.Offset > MaximumLatenessMilliseconds)
            {
                _emitted.Add(_scheduled.Dequeue().DisplayId);
                ExpiredCount++;
                _queueFullDrops++;
            }
            else
            {
                break;
            }
        }

        // Retry blocked entries next frame while allowing smaller entries to use free vertical space.
        int attempts = _pending.Count;
        for (int index = 0; index < attempts; index++)
        {
            (DanmakuDisplay next, double enqueued) = _pending.Dequeue();
            if (emit(next))
            {
                _emitted.Add(next.DisplayId);
                continue;
            }

            if (_elapsedMilliseconds - enqueued > MaximumPendingMilliseconds)
            {
                _emitted.Add(next.DisplayId);
                ExpiredCount++;
                _pendingExpiredDrops++;
            }
            else
            {
                _pending.Enqueue((next, enqueued));
            }
            _blockedEmissions++;
        }
        LogSchedulingSummary();
    }

    public void AddSaved(StoredDanmaku comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        if (comment.GameId != _gameId || comment.NodeId != _nodeId)
        {
            throw new ArgumentException("弹幕不属于当前游戏与节点。", nameof(comment));
        }

        if (_comments.TryAdd(comment.Id, comment))
        {
            string key = DanmakuAggregation.DisplayId(comment, AggregationEnabled);
            bool alreadyEmitted = _emitted.Contains(key);
            _displays.TryGetValue(key, out DanmakuDisplay? previous);
            StoredDanmaku representative = previous is null ? comment :
                DanmakuAggregation.Ordered([previous.Representative, comment]).First();
            var display = new DanmakuDisplay(key, representative, (previous?.Count ?? 0) + 1, representative.OffsetMilliseconds);
            if (previous is not null)
            {
                _scheduled.Remove(previous, out _, out _);
                int pending = _pending.Count;
                for (int index = 0; index < pending; index++)
                {
                    var entry = _pending.Dequeue();
                    if (entry.Comment.DisplayId != key) _pending.Enqueue(entry);
                }
            }

            _displays[key] = display;
            Schedule(display, alreadyEmitted ? OffsetMilliseconds : display.OffsetMilliseconds);
            DisplayVersion++;
        }
    }

    public void Replay()
    {
        LogSchedulingSummary(force: true);
        _elapsedMilliseconds = 0;
        _pending.Clear();
        _scheduled.Clear();
        _displays.Clear();
        _emitted.Clear();
        ExpiredCount = 0;
        foreach (IGrouping<string, StoredDanmaku> group in _comments.Values.GroupBy(comment => DanmakuAggregation.DisplayId(comment, AggregationEnabled)))
        {
            StoredDanmaku representative = DanmakuAggregation.Ordered(group).First();
            var display = new DanmakuDisplay(group.Key, representative, group.Count(), representative.OffsetMilliseconds);
            _displays.Add(group.Key, display);
            Schedule(display, display.OffsetMilliseconds);
        }

        ResetVersion++;
        DisplayVersion++;
        using var scope = RuntimeLog.BeginScope(new LogContext(GameId: _gameId, NodeId: _nodeId));
        RuntimeLog.Write(LogLevel.Information, "Playback", "PlaybackReplayed", new { count = _comments.Count, displayCount = _displays.Count, aggregationEnabled = AggregationEnabled });
    }

    private void LogSchedulingSummary(bool force = false)
    {
        if (_queueFullDrops == 0 && _pendingExpiredDrops == 0 && _blockedEmissions == 0) return;
        if (!force && Stopwatch.GetElapsedTime(_lastSchedulingLog).TotalSeconds < 5) return;
        using var scope = RuntimeLog.BeginScope(new LogContext(GameId: _gameId, NodeId: _nodeId));
        RuntimeLog.Write(LogLevel.Debug, "Playback", "SchedulingSummary", new
        {
            queueFullDrops = _queueFullDrops,
            pendingExpiredDrops = _pendingExpiredDrops,
            blockedEmissions = _blockedEmissions,
            pendingCount = _pending.Count,
            scheduledCount = _scheduled.Count,
            offsetMs = OffsetMilliseconds,
        });
        _queueFullDrops = _pendingExpiredDrops = _blockedEmissions = 0;
        _lastSchedulingLog = Stopwatch.GetTimestamp();
    }

    private void Schedule(DanmakuDisplay display, long offset) =>
        _scheduled.Enqueue(display, (offset, display.Representative.CreatedUtc.UtcTicks, display.Representative.Id));
}
