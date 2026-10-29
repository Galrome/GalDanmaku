using GalDanmaku.Core.GamePacks;

namespace GalDanmaku.Core.GameIdentity;

public sealed record GameIdentityResult(string DisplayName, string Source, string? GameId,
    string? WorkId, string? VndbId, string? BangumiId, IReadOnlyList<GameLibraryEntry> Candidates, string Reason,
    string? ConfirmedPathGameId = null);

public static class GameIdentityResolver
{
    public static GameIdentityResult Resolve(string? executablePath, LocalGameMappings mappings,
        IReadOnlyList<GameLibraryEntry> library)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return Unknown("无法读取进程路径，未识别游戏。");
        GameLibraryEntry[] candidates = library.Where(entry => LocalGameMappings.PathsEqual(entry.ExecutablePath, executablePath)).ToArray();
        LocalGameMatch exact = mappings.MatchExact(executablePath);
        if (exact.Status != LocalGameMatchStatus.Unknown)
        {
            if (exact.Game is { ProviderId: not null } saved && candidates.Length > 0)
            {
                GameLibraryEntry[] sameSource = candidates.Where(entry => entry.ProviderId == saved.ProviderId
                    && entry.SourceGameId == saved.SourceGameId).ToArray();
                GameLibraryEntry[] evidence = sameSource.Length > 0 ? sameSource : candidates;
                if (evidence.Any(entry => Conflicts(saved.WorkId, entry.WorkId) || Conflicts(saved.VndbId, entry.VndbId)
                    || Conflicts(saved.BangumiId, entry.BangumiId)) || sameSource.Length == 0 && candidates.Length > 1)
                    return FromLocal(exact) with { GameId = null, Candidates = candidates,
                        ConfirmedPathGameId = saved.RequiredGamePackId,
                        Reason = "缓存关联与当前游戏库身份存在冲突，已停止自动加载；请核对版本并重新确认，原 GameId 关联仍保留。" };
            }
            GameIdentityResult result = FromLocal(exact) with { Candidates = candidates, ConfirmedPathGameId = exact.Game?.RequiredGamePackId };
            if (exact.Game is { } local)
            {
                // Enrich display metadata without changing the already confirmed story namespace.
                GameLibraryEntry[] trusted = local.ProviderId is not null
                    ? candidates.Where(entry => entry.ProviderId == local.ProviderId && entry.SourceGameId == local.SourceGameId).ToArray()
                    : candidates;
                if (trusted.Length == 1 && !Conflicts(local.WorkId, trusted[0].WorkId)
                    && !Conflicts(local.VndbId, trusted[0].VndbId) && !Conflicts(local.BangumiId, trusted[0].BangumiId))
                    result = result with { WorkId = local.WorkId ?? trusted[0].WorkId,
                        VndbId = local.VndbId ?? trusted[0].VndbId, BangumiId = local.BangumiId ?? trusted[0].BangumiId,
                        Source = "已确认本机映射 + Luna 游戏库元数据" };
            }
            return result;
        }

        if (candidates.Length > 1)
            return new("游戏识别存在歧义", "Luna 游戏库", null, null, null, null, candidates,
                "同一实际 EXE 对应多个游戏库记录，请选择记录并确认适用的剧情包。");
        if (candidates.Length == 1)
        {
            GameLibraryEntry entry = candidates[0];
            LocalGameMatch source = mappings.MatchSource(entry.ProviderId, entry.SourceGameId);
            if (source.Status == LocalGameMatchStatus.Ambiguous) return FromLocal(source) with { Candidates = candidates };
            if (source.Game is { } saved)
                return new(entry.DisplayName, "Luna 来源关联 · 安装路径已变化", null, entry.WorkId, entry.VndbId,
                    entry.BangumiId, candidates, $"此来源记录曾关联 GameId {saved.RequiredGamePackId}，但实际 EXE 路径已变化；请核对版本并重新确认。");
            return new(entry.DisplayName, "Luna 游戏库 · 实际 EXE 路径", null, entry.WorkId, entry.VndbId,
                entry.BangumiId, candidates, "已识别作品，尚未确认 GameId；请选择适用于当前版本的剧情包并确认关联。");
        }

        // Retain the legacy fallback, but never reuse a new UID association based on EXE name alone.
        return FromLocal(mappings.MatchLegacy(executablePath));
    }

    private static GameIdentityResult FromLocal(LocalGameMatch match) => match.Game is { } game
        ? new(game.DisplayName, "已确认本机映射", game.RequiredGamePackId, game.WorkId, game.VndbId, game.BangumiId, [], match.Reason)
        : Unknown(match.Reason) with { DisplayName = match.Status == LocalGameMatchStatus.Ambiguous ? "游戏识别存在歧义" : "未识别游戏" };

    private static GameIdentityResult Unknown(string reason) => new("未识别游戏", "无可靠身份", null, null, null, null, [], reason);

    private static bool Conflicts(string? saved, string? current) => saved is not null && current is not null
        && !string.Equals(saved, current, StringComparison.Ordinal);
}
