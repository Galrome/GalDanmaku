using GalDanmaku.Core;

namespace GalDanmaku.App.ViewModels;

public sealed partial class MainViewModel
{
    private LocalLikeController _likes = null!;

    private void InitializeLikes(ILocalLikeRepository repository)
    {
        _likes = new LocalLikeController(repository);
        _likes.Changed += OnLikeStateChanged;
        _overlay.HoveredCommentChanged += OnHoveredCommentChanged;
        _overlay.LikeRequested += OnLikeRequested;
    }

    private void OnHoveredCommentChanged(object? sender, EventArgs args)
    {
        // Selection always advances the controller generation, including re-entry to the same record.
        _ = _likes.SelectAsync(_overlay.HoveredComment);
    }

    private void OnLikeRequested(object? sender, EventArgs args)
    {
        if (_disposed || _overlay.HoveredComment is not { } source || !ReferenceEquals(source, _likes.Selection)) return;
        _ = _likes.CanRetry ? _likes.RetryAsync() : _likes.ToggleAsync();
    }

    private void OnLikeStateChanged(object? sender, EventArgs args) =>
        _overlay.UpdateLikeState(_likes.IsLiked, _likes.IsBusy, _likes.Error);

    private async ValueTask DisposeLikesAsync()
    {
        _overlay.HoveredCommentChanged -= OnHoveredCommentChanged;
        _overlay.LikeRequested -= OnLikeRequested;
        _likes.Changed -= OnLikeStateChanged;
        await _likes.DisposeAsync();
    }
}
