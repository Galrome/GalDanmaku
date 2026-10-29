using System.Text.Json;
using GalDanmaku.Core.Diagnostics;

namespace GalDanmaku.Core.GamePacks;

public sealed record LocalGameMapping(string Id, string DisplayName, string ExecutableName,
    string ExecutablePath, string? GamePackId = null, string? ProviderId = null, string? SourceGameId = null,
    string? WorkId = null, string? VndbId = null, string? BangumiId = null, bool ExactPathOnly = false)
{
    public string RequiredGamePackId => GamePackId ?? Id;
}

public enum LocalGameMatchStatus { Unknown, Matched, Ambiguous }

public sealed record LocalGameMatch(LocalGameMatchStatus Status, LocalGameMapping? Game, string Reason);

// Local identity hints are separate from story packs: recognizing a game never supplies a story index.
public sealed class LocalGameMappings
{
    public static LocalGameMappings Empty { get; } = new([]);
    private const int MaximumFileBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 16, WriteIndented = true };
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private readonly IReadOnlyList<LocalGameMapping> _games;

    private LocalGameMappings(IReadOnlyList<LocalGameMapping> games) => _games = games;

    public static string ResolveConfigurationPath(string settingsDirectory, string userDirectory, string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath.Trim();
        // A development settings override may contain only Luna settings. Keep the user's existing
        // mapping preference available instead of falling back to an unrelated missing default file.
        foreach (string directory in new[] { settingsDirectory, userDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string pointer = Path.Combine(directory, "game-mapping-location.txt");
            try
            {
                if (!File.Exists(pointer)) continue;
                string path = File.ReadAllText(pointer).Trim();
                if (path.Length > 0) return Path.GetFullPath(path, Path.GetFullPath(directory));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                RuntimeLog.Write(LogLevel.Warning, "GameMapping", "MappingLocationReadFailed", new { pointer }, error);
            }
        }
        return Path.Combine(settingsDirectory, "game-mappings.local.json");
    }

    public static async Task<LocalGameMappings> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(path);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("本地游戏映射文件过大。");
        MappingFile file = await JsonSerializer.DeserializeAsync<MappingFile>(stream,
            JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("本地游戏映射为空。");
        if (file.Version != 1 || file.Games is null) throw new InvalidDataException("本地游戏映射必须使用 version 1 和 games 数组。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (LocalGameMapping game in file.Games)
        {
            if (game is null || string.IsNullOrWhiteSpace(game.Id) || string.IsNullOrWhiteSpace(game.DisplayName)
                || string.IsNullOrWhiteSpace(game.ExecutableName) || string.IsNullOrWhiteSpace(game.ExecutablePath)
                || game.GamePackId is not null && string.IsNullOrWhiteSpace(game.GamePackId)
                || (game.ProviderId is null) != (game.SourceGameId is null)
                || game.ProviderId is not null && (string.IsNullOrWhiteSpace(game.ProviderId) || string.IsNullOrWhiteSpace(game.SourceGameId))
                || game.ProviderId is not null && string.IsNullOrWhiteSpace(game.GamePackId)
                || !ids.Add(game.Id))
                throw new InvalidDataException("本地游戏映射的标识、名称和可执行文件不能为空，标识不得重复；来源关联必须同时提供 providerId、sourceGameId 和 gamePackId。");
            string normalized = NormalizePath(game.ExecutablePath);
            if (!string.Equals(Path.GetFileName(normalized), game.ExecutableName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("本地游戏映射的 executableName 与 executablePath 不一致。");
        }
        RuntimeLog.Write(LogLevel.Information, "GameMapping", "MappingConfigurationLoaded", new { path, count = file.Games.Count, file.Version });
        return new LocalGameMappings(file.Games.AsReadOnly());
    }

    public LocalGameMatch Match(string? executablePath)
        => MatchInternal(executablePath, exactOnly: false, legacyOnly: false);

    public LocalGameMatch MatchExact(string? executablePath)
        => MatchInternal(executablePath, exactOnly: true, legacyOnly: false);

    public LocalGameMatch MatchLegacy(string? executablePath)
        => MatchInternal(executablePath, exactOnly: false, legacyOnly: true);

    public LocalGameMatch MatchSource(string providerId, string sourceGameId)
    {
        LocalGameMapping[] candidates = _games.Where(game => game.ProviderId == providerId && game.SourceGameId == sourceGameId).ToArray();
        return MatchCandidates(candidates);
    }

    private static LocalGameMatch MatchCandidates(LocalGameMapping[] candidates)
    {
        // The same confirmed namespace can have several installation paths.
        string[] identities = candidates.Select(game => game.RequiredGamePackId).Distinct(StringComparer.Ordinal).ToArray();
        return identities.Length switch
        {
            1 => new(LocalGameMatchStatus.Matched, candidates[0], "已识别游戏。"),
            > 1 => new(LocalGameMatchStatus.Ambiguous, null, "本地游戏映射存在冲突，未自动选择。"),
            _ => new(LocalGameMatchStatus.Unknown, null, "本地游戏映射未命中，未识别游戏。")
        };
    }

    private LocalGameMatch MatchInternal(string? executablePath, bool exactOnly, bool legacyOnly)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            RuntimeLog.Write(LogLevel.Warning, "GameMapping", "GameRecognitionSkipped", new { reason = "ProcessPathUnavailable" });
            return new(LocalGameMatchStatus.Unknown, null, "无法读取进程路径，未识别游戏。");
        }
        string normalized = NormalizePath(executablePath);
        IEnumerable<LocalGameMapping> games = legacyOnly ? _games.Where(game => game.ProviderId is null && !game.ExactPathOnly) : _games;
        LocalGameMapping[] candidates = games.Where(game => string.Equals(NormalizePath(game.ExecutablePath), normalized,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        bool exactPath = candidates.Length > 0;
        if (candidates.Length == 0 && !exactOnly)
        {
            string name = Path.GetFileName(normalized);
            candidates = games.Where(game => !game.ExactPathOnly && game.ProviderId is null
                && string.Equals(game.ExecutableName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        RuntimeLog.Write(candidates.Length == 1 ? LogLevel.Information : LogLevel.Warning, "GameMapping",
            candidates.Length == 1 ? "GameMappingMatched" : candidates.Length > 1 ? "GameMappingAmbiguous" : "GameMappingUnknown",
            new { executablePath = normalized, method = exactPath || exactOnly ? "FullPath" : "ExecutableName", candidateCount = candidates.Length,
                gameId = candidates.Length == 1 ? candidates[0].RequiredGamePackId : null });
        return MatchCandidates(candidates);
    }

    public static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    // Caller supplies an app-owned destination. An existing invalid file is never overwritten.
    public static async Task SaveConfirmationAsync(string sourcePath, string destinationPath, LocalGameMapping confirmation,
        CancellationToken cancellationToken = default, string? expectedGameId = null)
    {
        await SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveConfirmationCoreAsync(sourcePath, destinationPath, confirmation, cancellationToken, expectedGameId).ConfigureAwait(false); }
        finally { SaveGate.Release(); }
    }

    private static async Task SaveConfirmationCoreAsync(string sourcePath, string destinationPath, LocalGameMapping confirmation,
        CancellationToken cancellationToken, string? expectedGameId)
    {
        LocalGameMappings current;
        try { current = await LoadAsync(sourcePath, cancellationToken).ConfigureAwait(false); }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException) { current = Empty; }
        string destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetFullPath(sourcePath), destination, StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
        {
            LocalGameMappings existing = await LoadAsync(destination, cancellationToken).ConfigureAwait(false);
            var merged = current._games.ToList();
            foreach (LocalGameMapping saved in existing._games)
            {
                LocalGameMapping? sameId = merged.FirstOrDefault(game => game.Id == saved.Id);
                if (sameId is not null && sameId != saved)
                    throw new InvalidDataException("导入配置与本机已有身份冲突，未覆盖现有文件。");
                if (sameId is null) merged.Add(saved);
            }
            current = new(merged.AsReadOnly());
        }
        LocalGameMatch pathMatch = current.MatchExact(confirmation.ExecutablePath);
        if (expectedGameId is not null && (pathMatch.Game is null || pathMatch.Game.RequiredGamePackId != expectedGameId))
            throw new InvalidDataException("原关联已变化，请重新识别后再发起重新关联。");
        if (pathMatch.Status == LocalGameMatchStatus.Ambiguous || expectedGameId is null
            && pathMatch.Game is { } game && game.RequiredGamePackId != confirmation.RequiredGamePackId)
            throw new InvalidDataException("已确认的身份存在冲突；不会覆盖或改指向其他 GameId。");
        // Explicit reassociation affects this installation only, never other paths sharing the UID.
        if (confirmation.ProviderId is not null && current._games.Any(saved => saved.ProviderId == confirmation.ProviderId
            && saved.SourceGameId == confirmation.SourceGameId && !PathsEqual(saved.ExecutablePath, confirmation.ExecutablePath)
            && saved.RequiredGamePackId != confirmation.RequiredGamePackId))
            throw new InvalidDataException("同一来源 UID 在其他安装路径上关联了不同 GameId，未修改关联。");

        var games = current._games.ToList();
        if (pathMatch.Game is null) games.Add(confirmation with { ExactPathOnly = true });
        else
            for (int index = 0; index < games.Count; index++)
                if (PathsEqual(games[index].ExecutablePath, confirmation.ExecutablePath))
                    games[index] = confirmation with { Id = games[index].Id, ExactPathOnly = true };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new MappingFile(1, games), JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("本地游戏映射文件过大。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string pending = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(pending, bytes, cancellationToken).ConfigureAwait(false);
            // Reuse the reader's validation before committing an atomic replacement.
            await LoadAsync(pending, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, destination, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    private static string NormalizePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("可执行文件路径必须是完整路径。");
        return Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private sealed record MappingFile(int Version, List<LocalGameMapping>? Games);
}
