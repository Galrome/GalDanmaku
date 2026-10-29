namespace GalDanmaku.Core.GameIdentity;

public interface IGameLibraryProvider
{
    string ProviderId { get; }
    Task<IReadOnlyList<GameLibraryEntry>> GetGamesAsync(CancellationToken cancellationToken = default);
}
