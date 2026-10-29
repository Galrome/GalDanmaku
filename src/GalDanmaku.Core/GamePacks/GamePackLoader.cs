using GalDanmaku.Core.Story;

namespace GalDanmaku.Core.GamePacks;

public static class GamePackLoader
{
    // Reads the manifest and its story index, then checks both name the same game.
    public static async Task<LoadedGamePack> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        ParsedManifest manifest = await GamePackManifest.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
        string indexPath = GamePackFiles.ResolveInside(manifest.Pack.Directory, manifest.StoryIndexPath);
        StoryIndex index = await StoryIndex.LoadAsync(indexPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(index.GameId, manifest.Pack.GameId, StringComparison.Ordinal))
            throw new InvalidDataException("剧情索引的 GameId 与游戏包清单不一致。");
        return new LoadedGamePack(manifest.Pack, index);
    }
}
