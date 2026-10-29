namespace GalDanmaku.Integrations.LunaTranslator;

internal sealed record LunaGameRecord(string Uid, string GamePath, string Title,
    string? VndbId, string? BangumiId, string? LaunchPath, string? LaunchMethod);

internal sealed record LunaGameDataSnapshot(IReadOnlyList<LunaGameRecord> Games, int SkippedRecords, int UnlistedRecords);
