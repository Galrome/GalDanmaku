namespace GalDanmaku.Core;

public sealed class NodePlayback
{
    public const int MaximumPendingComments = 128;
    private readonly Dictionary<Guid, StoredDanmaku> _comments = [];
    private readonly PriorityQueue<StoredDanmaku, (long Offset, long Created, Guid Id)> _scheduled = new();
    private readonly Queue<StoredDanmaku> _pending = new();
    private string _gameId = "";
    private string _nodeId = "";
    private double _elapsedMilliseconds;

    public bool IsPlaying { get; set; }
    public long OffsetMilliseconds => (long)Math.Min(_elapsedMilliseconds, long.MaxValue - 1024d);
    public int PendingCount => _pending.Count;
    public int SavedCount => _comments.Count;
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

    public void Advance(double seconds, Func<StoredDanmaku, bool> emit)
    {
        DanmakuEngine.ValidateNonNegativeFinite(seconds, nameof(seconds));
        ArgumentNullException.ThrowIfNull(emit);
        if (!IsPlaying)
        {
            return;
        }

        _elapsedMilliseconds += seconds * 1000;
        while (_pending.Count < MaximumPendingComments &&
            _scheduled.TryPeek(out StoredDanmaku? comment, out _) && comment.OffsetMilliseconds <= _elapsedMilliseconds)
        {
            _pending.Enqueue(_scheduled.Dequeue());
        }

        // Retry blocked entries next frame while allowing smaller entries to use free vertical space.
        int attempts = _pending.Count;
        for (int index = 0; index < attempts; index++)
        {
            StoredDanmaku next = _pending.Dequeue();
            if (!emit(next))
            {
                _pending.Enqueue(next);
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
        foreach (StoredDanmaku comment in _comments.Values)
        {
            Schedule(comment);
        }

        ResetVersion++;
    }

    private void Schedule(StoredDanmaku comment) =>
        _scheduled.Enqueue(comment, (comment.OffsetMilliseconds, comment.CreatedUtc.UtcTicks, comment.Id));
}
