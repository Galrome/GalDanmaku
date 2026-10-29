using GalDanmaku.Core.Story;
using GalDanmaku.Core.Diagnostics;
using System.Diagnostics;

namespace GalDanmaku.Core;

public enum NodeBindingState { Idle, Loading, Active, Failed }

// Keeps the comment session bound to the node the locator currently confirms.
// All members must be called on the owning (UI) thread; StateChanged is raised on that thread too.
// Location changes are latest-wins: obsolete loads are discarded and session operations never overlap.
public sealed class NodeBindingCoordinator : IDisposable
{
    private readonly LocalCommentSession _session;
    private readonly string _gameId;
    private long _generation;
    private string? _targetNodeId;
    private long _targetVersion;
    private LogContext _targetContext = new();
    private CancellationTokenSource? _loadCancellation;
    private bool _pumping;
    private int _activeSends;
    private TaskCompletionSource? _sendsDrained;
    private bool _disposed;

    public NodeBindingCoordinator(LocalCommentSession session, string gameId)
    {
        ArgumentNullException.ThrowIfNull(session);
        StoredDanmaku.ValidateIdentity(gameId, nameof(gameId));
        _session = session;
        _gameId = gameId;
    }

    public NodeBindingState State { get; private set; } = NodeBindingState.Idle;
    public string? BoundNodeId { get; private set; }
    public string? LastError { get; private set; }
    public bool CanSend => !_disposed && State == NodeBindingState.Active && BoundNodeId is not null &&
        BoundNodeId == _targetNodeId && _session.IsLoaded && _session.NodeId == BoundNodeId;
    public event EventHandler? StateChanged;

    public void OnLocationChanged(StoryLocation location, long confirmationVersion)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (_disposed)
        {
            RuntimeLog.Write(LogLevel.Debug, "NodeBinding", "NodeBindingSkipped", new { reason = "Disposed" });
            return;
        }

        if (location.Status != StoryLocationStatus.Confirmed || location.Node is null)
        {
            RuntimeLog.Write(LogLevel.Debug, "NodeBinding", "NodeBindingSkipped", new { reason = "Unconfirmed", status = location.Status.ToString() });
            Invalidate();
            return;
        }

        string nodeId = location.Node.Id;
        if (_targetNodeId == nodeId && _targetVersion == confirmationVersion)
        {
            RuntimeLog.Write(LogLevel.Debug, "NodeBinding", "NodeBindingSkipped", new { reason = "SameConfirmation", nodeId, confirmationVersion });
            return;
        }

        // New node, or re-entry of the same node: drop whatever is bound or loading right now.
        _generation++;
        CancelLoad();
        _targetNodeId = nodeId;
        _targetVersion = confirmationVersion;
        _targetContext = RuntimeLog.CurrentContext with { GameId = _gameId, NodeId = nodeId };
        using var requestScope = RuntimeLog.BeginScope(_targetContext);
        RuntimeLog.Write(LogLevel.Information, "NodeBinding", "NodeBindRequested", new { confirmationVersion, generation = _generation });
        _loadCancellation = new CancellationTokenSource();
        _session.Unload();
        SetState(NodeBindingState.Loading, null, null);
        if (!_pumping)
        {
            _pumping = true;
            _ = PumpAsync();
        }
    }

    // Failed keeps the target, so a transient storage error can be retried without waiting for a new confirmation.
    public void Retry()
    {
        if (_disposed || State != NodeBindingState.Failed || _targetNodeId is null)
        {
            RuntimeLog.Write(LogLevel.Debug, "NodeBinding", "NodeBindingSkipped", new { reason = _disposed ? "Disposed" : "RetryUnavailable", state = State.ToString() });
            return;
        }

        _generation++;
        CancelLoad();
        _loadCancellation = new CancellationTokenSource();
        using var requestScope = RuntimeLog.BeginScope(_targetContext);
        RuntimeLog.Write(LogLevel.Information, "NodeBinding", "NodeBindRequested", new { reason = "Retry", generation = _generation });
        _session.Unload();
        SetState(NodeBindingState.Loading, null, null);
        if (!_pumping)
        {
            _pumping = true;
            _ = PumpAsync();
        }
    }

    public async Task<StoredDanmaku> SendAsync(string text, string color, double fontSize, CancellationToken cancellationToken = default)
    {
        if (!CanSend)
        {
            RuntimeLog.Write(LogLevel.Debug, "NodeBinding", "NodeSendSkipped", new { reason = "NodeNotConfirmed", state = State.ToString() });
            throw new InvalidOperationException("当前节点未确认，无法发送。");
        }

        _activeSends++;
        try
        {
            return await _session.SendAsync(text, color, fontSize, cancellationToken);
        }
        finally
        {
            if (--_activeSends == 0)
            {
                _sendsDrained?.TrySetResult();
                _sendsDrained = null;
            }
        }
    }

    public void Reset()
    {
        if (!_disposed)
        {
            Invalidate();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RuntimeLog.Write(LogLevel.Debug, "NodeBinding", "NodeBindingDisposed", new { nodeId = BoundNodeId });
        _generation++;
        CancelLoad();
        _targetNodeId = null;
        _session.Unload();
        State = NodeBindingState.Idle;
        BoundNodeId = null;
        LastError = null;
        _sendsDrained?.TrySetResult();
        _sendsDrained = null;
    }

    private void Invalidate()
    {
        _generation++;
        CancelLoad();
        _targetNodeId = null;
        _session.Unload();
        SetState(NodeBindingState.Idle, null, null);
    }

    private void CancelLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    // Single pump: it always applies the latest target, so session operations never overlap.
    private async Task PumpAsync()
    {
        try
        {
            while (!_disposed && State == NodeBindingState.Loading)
            {
                if (_activeSends > 0)
                {
                    // A save in flight keeps the session busy; apply the latest target after it ends.
                    _sendsDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await _sendsDrained.Task;
                    continue;
                }

                long generation = _generation;
                string nodeId = _targetNodeId!;
                CancellationToken token = _loadCancellation!.Token;
                // The pump may have started for an older dialogue. Use this request's captured context.
                using var loadScope = RuntimeLog.BeginScope(_targetContext with { OperationId = Guid.NewGuid().ToString("N") });
                long started = Stopwatch.GetTimestamp();
                RuntimeLog.Write(LogLevel.Information, "NodeBinding", "NodeLoadStarted", new { generation });
                try
                {
                    await _session.LoadAsync(_gameId, nodeId, token);
                }
                catch (Exception exception) when (generation != _generation || _disposed)
                {
                    if (exception is OperationCanceledException)
                        RuntimeLog.Write(LogLevel.Debug, "NodeBinding", "NodeLoadCancelled", new { generation, superseded = true, durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                    RuntimeLog.Write(exception is OperationCanceledException ? LogLevel.Debug : LogLevel.Warning,
                        "NodeBinding", "NodeLoadSuperseded", new { generation, disposed = _disposed, durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }, exception);
                    // Superseded load: its outcome, including failure, no longer matters.
                    continue;
                }
                catch (Exception exception)
                {
                    RuntimeLog.Write(exception is OperationCanceledException ? LogLevel.Debug : LogLevel.Error,
                        "NodeBinding", exception is OperationCanceledException ? "NodeLoadCancelled" : "NodeLoadFailed",
                        new { generation, durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }, exception);
                    _session.Unload();
                    SetState(NodeBindingState.Failed, null, exception.Message);
                    continue;
                }

                if (generation != _generation)
                {
                    RuntimeLog.Write(LogLevel.Debug, "NodeBinding", "NodeLoadSuperseded", new { generation, durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                    _session.Unload();
                    continue;
                }

                _session.Playback.IsPlaying = true;
                SetState(NodeBindingState.Active, nodeId, null);
                RuntimeLog.Write(LogLevel.Information, "NodeBinding", "NodeBound", new { count = _session.Playback.SavedCount, generation, durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    private void SetState(NodeBindingState state, string? boundNodeId, string? error)
    {
        if (State == state && BoundNodeId == boundNodeId && LastError == error)
        {
            return;
        }

        State = state;
        BoundNodeId = boundNodeId;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
