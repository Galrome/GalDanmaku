using System.Text.Json;
using GalDanmaku.Core.Diagnostics;

namespace GalDanmaku.Core.GamePacks;

public sealed record LocalGameMapping(string Id, string DisplayName, string ExecutableName,
    string ExecutablePath, string? GamePackId = null)
{
    public string RequiredGamePackId => GamePackId ?? Id;
}

public enum LocalGameMatchStatus { Unknown, Matched, Ambiguous }

public sealed record LocalGameMatch(LocalGameMatchStatus Status, LocalGameMapping? Game, string Reason);

// Local identity hints are separate from story packs: recognizing a game never supplies a story index.
public sealed class LocalGameMappings
{
    private readonly IReadOnlyList<LocalGameMapping> _games;

    private LocalGameMappings(IReadOnlyList<LocalGameMapping> games) => _games = games;

    public static async Task<LocalGameMappings> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(path);
        if (stream.Length > 256 * 1024) throw new InvalidDataException("本地游戏映射文件过大。");
        MappingFile file = await JsonSerializer.DeserializeAsync<MappingFile>(stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken)
            ?? throw new InvalidDataException("本地游戏映射为空。");
        if (file.Version != 1 || file.Games is null) throw new InvalidDataException("本地游戏映射必须使用 version 1 和 games 数组。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (LocalGameMapping game in file.Games)
        {
            if (game is null || string.IsNullOrWhiteSpace(game.Id) || string.IsNullOrWhiteSpace(game.DisplayName)
                || string.IsNullOrWhiteSpace(game.ExecutableName) || string.IsNullOrWhiteSpace(game.ExecutablePath)
                || game.GamePackId is not null && string.IsNullOrWhiteSpace(game.GamePackId)
                || !ids.Add(game.Id))
                throw new InvalidDataException("本地游戏映射的标识、名称和可执行文件不能为空，标识不得重复。");
            string normalized = NormalizePath(game.ExecutablePath);
            if (!string.Equals(Path.GetFileName(normalized), game.ExecutableName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("本地游戏映射的 executableName 与 executablePath 不一致。");
        }
        RuntimeLog.Write(LogLevel.Information, "GameMapping", "MappingConfigurationLoaded", new { path, count = file.Games.Count, file.Version });
        return new LocalGameMappings(file.Games.AsReadOnly());
    }

    public LocalGameMatch Match(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            RuntimeLog.Write(LogLevel.Warning, "GameMapping", "GameRecognitionSkipped", new { reason = "ProcessPathUnavailable" });
            return new(LocalGameMatchStatus.Unknown, null, "无法读取进程路径，未识别游戏。");
        }
        string normalized = NormalizePath(executablePath);
        LocalGameMapping[] candidates = _games.Where(game => string.Equals(NormalizePath(game.ExecutablePath), normalized,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        bool exactPath = candidates.Length > 0;
        if (candidates.Length == 0)
        {
            string name = Path.GetFileName(normalized);
            candidates = _games.Where(game => string.Equals(game.ExecutableName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        RuntimeLog.Write(candidates.Length == 1 ? LogLevel.Information : LogLevel.Warning, "GameMapping",
            candidates.Length == 1 ? "GameMappingMatched" : candidates.Length > 1 ? "GameMappingAmbiguous" : "GameMappingUnknown",
            new { executablePath = normalized, method = exactPath ? "FullPath" : "ExecutableName", candidateCount = candidates.Length,
                gameId = candidates.Length == 1 ? candidates[0].Id : null });
        return candidates.Length switch
        {
            1 => new(LocalGameMatchStatus.Matched, candidates[0], "已识别游戏。"),
            > 1 => new(LocalGameMatchStatus.Ambiguous, null, "本地游戏映射存在多个候选，未自动选择。"),
            _ => new(LocalGameMatchStatus.Unknown, null, "本地游戏映射未命中，未识别游戏。")
        };
    }

    private static string NormalizePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("可执行文件路径必须是完整路径。");
        return Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private sealed record MappingFile(int Version, List<LocalGameMapping>? Games);
}
