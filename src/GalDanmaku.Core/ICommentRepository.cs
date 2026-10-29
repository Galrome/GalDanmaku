namespace GalDanmaku.Core;

public interface ICommentRepository
{
    Task<IReadOnlyList<StoredDanmaku>> LoadAsync(string gameId, string nodeId, CancellationToken cancellationToken = default);
    Task SaveAsync(StoredDanmaku comment, CancellationToken cancellationToken = default);
}
