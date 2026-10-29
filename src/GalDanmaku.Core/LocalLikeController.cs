namespace GalDanmaku.Core;

// Owned by the UI thread. Repository work is serialized across selections, including cancellation:
// an old write must actually finish before re-entering the same comment can read or write its state.
public sealed class LocalLikeController(ILocalLikeRepository repository) : IAsyncDisposable
{
    private Task _tail = Task.CompletedTask;
    private CancellationTokenSource? _selectionCancellation;
    private long _generation;
    private bool _disposed;

    public StoredDanmaku? Selection { get; private set; }
    public bool? IsLiked { get; private set; }
    public bool IsBusy { get; private set; }
    public string? Error { get; private set; }
    public bool CanToggle => !_disposed && Selection is not null && IsLiked.HasValue && !IsBusy;
    public bool CanRetry => !_disposed && Selection is not null && Error is not null && !IsBusy;
    public event EventHandler? Changed;

    public Task SelectAsync(StoredDanmaku? comment)
    {
        if (_disposed) return Task.CompletedTask;
        long generation = ++_generation;
        CancelSelection();
        Selection = comment;
        IsLiked = null;
        Error = null;
        IsBusy = comment is not null;
        _selectionCancellation = comment is null ? null : new CancellationTokenSource();
        Changed?.Invoke(this, EventArgs.Empty);
        if (comment is null) return Task.CompletedTask;
        return Enqueue(comment, generation, _selectionCancellation!.Token, desired: null);
    }

    public Task ToggleAsync()
    {
        if (!CanToggle) return Task.CompletedTask;
        return BeginOperation(!IsLiked!.Value);
    }

    public Task RetryAsync()
    {
        if (!CanRetry) return Task.CompletedTask;
        return BeginOperation(IsLiked.HasValue ? !IsLiked.Value : null);
    }

    private Task BeginOperation(bool? desired)
    {
        IsBusy = true;
        Error = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return Enqueue(Selection!, _generation, _selectionCancellation!.Token, desired);
    }

    private Task Enqueue(StoredDanmaku comment, long generation, CancellationToken token, bool? desired)
    {
        _tail = RunAsync(_tail, comment, generation, token, desired);
        return _tail;
    }

    private async Task RunAsync(Task previous, StoredDanmaku comment, long generation, CancellationToken token, bool? desired)
    {
        await previous;
        if (!IsCurrent(generation) || token.IsCancellationRequested) return;
        try
        {
            bool liked;
            if (desired is { } value)
            {
                await repository.SetLocalLikedAsync(comment.GameId, comment.NodeId, comment.Id, value, token);
                liked = value;
            }
            else
            {
                liked = await repository.GetLocalLikedAsync(comment.GameId, comment.NodeId, comment.Id, token);
            }
            if (IsCurrent(generation)) IsLiked = liked;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsCurrent(generation)) Error = desired.HasValue ? "保存本地点赞失败，请点击重试。" : "读取本地点赞失败，请点击重试。";
        }
        finally
        {
            if (IsCurrent(generation))
            {
                IsBusy = false;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private bool IsCurrent(long generation) => !_disposed && _generation == generation;

    private void CancelSelection()
    {
        CancellationTokenSource? cancellation = _selectionCancellation;
        if (cancellation is null) return;
        cancellation.Cancel();
        // A driver may still register on the token while unwinding a canceled operation.
        // Dispose its source only once every operation issued with it has really returned.
        _ = DisposeCancellationAsync(_tail, cancellation);
        _selectionCancellation = null;
    }

    private static async Task DisposeCancellationAsync(Task operation, CancellationTokenSource cancellation)
    {
        try { await operation; }
        finally { cancellation.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _generation++;
            CancelSelection();
            Selection = null;
            IsLiked = null;
            IsBusy = false;
            Error = null;
            Changed = null;
        }
        await _tail;
    }
}
