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
    private readonly PriorityQueue<StoredDanmaku, (long Offset, long Created, Guid Id)> _scheduled = new();
    private readonly Queue<(StoredDanmaku Comment, double EnqueuedMilliseconds)> _pending = new();
    private string _gameId = "";
    private string _nodeId = "";
    private double _elapsedMilliseconds;

    public bool IsPlaying { get; set; }
    public long OffsetMilliseconds => (long)_elapsedMilliseconds;
    public int PendingCount => _pending.Count;
    public int SavedCount => _comments.Count;
    // Comments dropped as expired since the last Load/Replay/Clear; they stay saved and return on replay.
    public long ExpiredCount { get; private set; }
    public long ResetVersion { get; private set; }

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

        _gameId = gameId;
        _nodeId = nodeId;
        _comments.Clear();
        foreach (StoredDanmaku comment in loaded)
        {
            _comments.TryAdd(comment.Id, comment);
        }

        Replay();
    }

    public void Clear()
    {
        IsPlaying = false;
        _gameId = "";
        _nodeId = "";
        _comments.Clear();
        _elapsedMilliseconds = 0;
        _pending.Clear();
        _scheduled.Clear();
        ExpiredCount = 0;
        ResetVersion++;
    }

    public void Advance(double seconds, Func<StoredDanmaku, bool> emit)
    {
        DanmakuEngine.ValidateNonNegativeFinite(seconds, nameof(seconds));
        ArgumentNullException.ThrowIfNull(emit);
        if (!IsPlaying)
        {
            return;
        }

        _elapsedMilliseconds = Math.Min(_elapsedMilliseconds + seconds * 1000, MaximumElapsedMilliseconds);
        while (_scheduled.TryPeek(out StoredDanmaku? comment, out _) && comment.OffsetMilliseconds <= _elapsedMilliseconds)
        {
            if (_pending.Count < MaximumPendingComments)
            {
                _pending.Enqueue((_scheduled.Dequeue(), _elapsedMilliseconds));
            }
            else if (_elapsedMilliseconds - comment.OffsetMilliseconds > MaximumLatenessMilliseconds)
            {
                _scheduled.Dequeue();
                ExpiredCount++;
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
            (StoredDanmaku next, double enqueued) = _pending.Dequeue();
            if (emit(next))
            {
                continue;
            }

            if (_elapsedMilliseconds - enqueued > MaximumPendingMilliseconds)
            {
                ExpiredCount++;
            }
            else
            {
                _pending.Enqueue((next, enqueued));
            }
        }
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
            Schedule(comment);
        }
    }

    public void Replay()
    {
        _elapsedMilliseconds = 0;
        _pending.Clear();
        _scheduled.Clear();
        ExpiredCount = 0;
        foreach (StoredDanmaku comment in _comments.Values)
        {
            Schedule(comment);
        }

        ResetVersion++;
    }

    private void Schedule(StoredDanmaku comment) =>
        _scheduled.Enqueue(comment, (comment.OffsetMilliseconds, comment.CreatedUtc.UtcTicks, comment.Id));
}
