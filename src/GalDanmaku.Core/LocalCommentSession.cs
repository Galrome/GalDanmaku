namespace GalDanmaku.Core;

public sealed class LocalCommentSession : IDisposable
{
    private readonly ICommentRepository _repository;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public LocalCommentSession(ICommentRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public NodePlayback Playback { get; } = new();
    public string GameId { get; private set; } = "";
    public string NodeId { get; private set; } = "";
    public bool IsLoaded { get; private set; }
    public bool IsBusy { get; private set; }

    // Session operations are initiated on the owning thread (the UI dispatcher in the app).
    // Holding the busy state across await prevents a saved result from reaching a different node.
    public async Task LoadAsync(string gameId, string nodeId, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        StoredDanmaku.ValidateIdentity(gameId, nameof(gameId));
        StoredDanmaku.ValidateIdentity(nodeId, nameof(nodeId));
        Playback.IsPlaying = false;
        IsBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        try
        {
            IReadOnlyList<StoredDanmaku> comments = await _repository.LoadAsync(gameId, nodeId, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            Playback.Load(gameId, nodeId, comments);
            GameId = gameId;
            NodeId = nodeId;
            IsLoaded = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<StoredDanmaku> SendAsync(string text, string color, double fontSize, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        if (!IsLoaded)
        {
            throw new InvalidOperationException("节点尚未加载，请先重新加载。");
        }

        var comment = new StoredDanmaku(Guid.NewGuid(), GameId, NodeId, text, DanmakuType.Scrolling,
            color, fontSize, Playback.OffsetMilliseconds, DateTimeOffset.UtcNow);
        IsBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        try
        {
            await _repository.SaveAsync(comment, cancellation.Token);
            if (_disposed)
            {
                throw new OperationCanceledException("会话已关闭。", cancellation.Token);
            }

            Playback.AddSaved(comment);
            return comment;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsLoaded = false;
        Playback.IsPlaying = false;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy)
        {
            throw new InvalidOperationException("正在保存或加载弹幕，请稍后重试。");
        }
    }
}
