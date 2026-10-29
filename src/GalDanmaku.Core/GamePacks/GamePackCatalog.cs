namespace GalDanmaku.Core.GamePacks;

public sealed record GamePackEntry(string Directory, string DisplayName, string? GameId, string? Version,
    IReadOnlyList<string> ProcessNames, string? Error);

public static class GamePackCatalog
{
    public const int MaximumPacks = 100;

    // Lists first-level sub-directories holding a gamepack.json. Only manifests are read; one broken
    // pack is reported through Error and never stops the others.
    public static async Task<IReadOnlyList<GamePackEntry>> DiscoverAsync(string root, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(root)) return [];
        string[] candidates = await Task.Run(() =>
        {
            string fullRoot = Path.GetFullPath(root);
            if (!System.IO.Directory.Exists(fullRoot)) return Array.Empty<string>();
            return System.IO.Directory.EnumerateDirectories(fullRoot)
                .Where(directory => File.Exists(Path.Combine(directory, GamePackFiles.ManifestFileName)))
                .Order(StringComparer.Ordinal).Take(MaximumPacks).ToArray();
        }, cancellationToken).ConfigureAwait(false);

        var entries = new List<GamePackEntry>();
        foreach (string directory in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                GamePack pack = (await GamePackManifest.ReadAsync(directory, cancellationToken).ConfigureAwait(false)).Pack;
                entries.Add(new GamePackEntry(pack.Directory, pack.Name, pack.GameId, pack.Version, pack.ProcessNames, null));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                entries.Add(new GamePackEntry(Path.GetFullPath(directory), Path.GetFileName(directory), null, null, [], exception.Message));
            }
        }
        return entries.AsReadOnly();
    }
}
