using System.Diagnostics;
using GalDanmaku.Core.Diagnostics;

namespace GalDanmaku.Core;

public sealed class LocalCommentSession : IDisposable
{
    private readonly ICommentRepository _repository;
    private readonly CancellationTokenSource _lifetime = new();
    private long _unloadVersion;
    private bool _disposed;
    private LogContext _nodeContext = new();

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
        using var scope = RuntimeLog.BeginScope(new LogContext(GameId: gameId, NodeId: nodeId, OperationId: Guid.NewGuid().ToString("N")));
        long started = Stopwatch.GetTimestamp();
        RuntimeLog.Write(LogLevel.Debug, "LocalSession", "SessionLoadStarted");
        try
        {
            EnsureAvailable();
            StoredDanmaku.ValidateIdentity(gameId, nameof(gameId));
            StoredDanmaku.ValidateIdentity(nodeId, nameof(nodeId));
            Playback.IsPlaying = false;
            IsBusy = true;
            long unloadVersion = _unloadVersion;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            try
            {
                IReadOnlyList<StoredDanmaku> comments = await _repository.LoadAsync(gameId, nodeId, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (unloadVersion != _unloadVersion)
                {
                    throw new OperationCanceledException("加载期间会话已卸载。", cancellation.Token);
                }

                Playback.Load(gameId, nodeId, comments);
                GameId = gameId;
                NodeId = nodeId;
                IsLoaded = true;
                _nodeContext = RuntimeLog.CurrentContext;
                RuntimeLog.Write(LogLevel.Information, "LocalSession", "SessionLoadSucceeded", new { count = comments.Count, durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException exception)
        {
            RuntimeLog.Write(LogLevel.Debug, "LocalSession", "SessionLoadCancelled", new { durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }, exception);
            throw;
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(LogLevel.Error, "LocalSession", "SessionLoadFailed", new { durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }, exception);
            throw;
        }
    }

    public async Task<StoredDanmaku> SendAsync(string text, string color, double fontSize, CancellationToken cancellationToken = default)
    {
        using var scope = RuntimeLog.BeginScope(_nodeContext with { OperationId = Guid.NewGuid().ToString("N") });
        long started = Stopwatch.GetTimestamp();
        RuntimeLog.Write(LogLevel.Debug, "LocalSession", "SessionSaveStarted");
        try
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

                // The node may have been unloaded while saving; the comment is stored but must not join another timeline.
                if (IsLoaded && GameId == comment.GameId && NodeId == comment.NodeId)
                {
                    Playback.AddSaved(comment);
                }

                RuntimeLog.Write(LogLevel.Information, "LocalSession", "SessionSaveSucceeded", new { commentId = comment.Id, queued = IsLoaded && GameId == comment.GameId && NodeId == comment.NodeId, durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                return comment;
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException exception)
        {
            RuntimeLog.Write(LogLevel.Debug, "LocalSession", "SessionSaveCancelled", new { durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }, exception);
            throw;
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(LogLevel.Error, "LocalSession", "SessionSaveFailed", new { durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }, exception);
            throw;
        }
    }

    // Clears the loaded node so nothing can be sent or played until the next LoadAsync.
    // Allowed while busy: an in-flight load is discarded and an in-flight save is stored without queuing.
    // No-op after Dispose, which has already cleared the state.
    public void Unload()
    {
        if (_disposed)
        {
            return;
        }

        using var scope = RuntimeLog.BeginScope(_nodeContext);
        RuntimeLog.Write(LogLevel.Debug, "LocalSession", "SessionUnloaded", new { loaded = IsLoaded, busy = IsBusy, count = Playback.SavedCount });
        _unloadVersion++;
        IsLoaded = false;
        GameId = "";
        NodeId = "";
        Playback.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        using var scope = RuntimeLog.BeginScope(_nodeContext);
        RuntimeLog.Write(LogLevel.Debug, "LocalSession", "SessionDisposed", new { loaded = IsLoaded, busy = IsBusy });
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
