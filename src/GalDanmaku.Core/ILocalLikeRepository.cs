namespace GalDanmaku.Core;

public interface ILocalLikeRepository
{
    Task<bool> GetLocalLikedAsync(string gameId, string nodeId, Guid commentId, CancellationToken cancellationToken = default);
    Task SetLocalLikedAsync(string gameId, string nodeId, Guid commentId, bool liked, CancellationToken cancellationToken = default);
}
