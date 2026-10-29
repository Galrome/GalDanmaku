using GalDanmaku.Core.Story;

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
            return;
        }

        if (location.Status != StoryLocationStatus.Confirmed || location.Node is null)
        {
            Invalidate();
            return;
        }

        string nodeId = location.Node.Id;
        if (_targetNodeId == nodeId && _targetVersion == confirmationVersion)
        {
            return;
        }

        // New node, or re-entry of the same node: drop whatever is bound or loading right now.
        _generation++;
        CancelLoad();
        _targetNodeId = nodeId;
        _targetVersion = confirmationVersion;
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
            return;
        }

        _generation++;
        CancelLoad();
        _loadCancellation = new CancellationTokenSource();
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
                try
                {
                    await _session.LoadAsync(_gameId, nodeId, token);
                }
                catch (Exception) when (generation != _generation || _disposed)
                {
                    // Superseded load: its outcome, including failure, no longer matters.
                    continue;
                }
                catch (Exception exception)
                {
                    _session.Unload();
                    SetState(NodeBindingState.Failed, null, exception.Message);
                    continue;
                }

                if (generation != _generation)
                {
                    _session.Unload();
                    continue;
                }

                _session.Playback.IsPlaying = true;
                SetState(NodeBindingState.Active, nodeId, null);
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
