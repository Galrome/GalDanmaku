namespace GalDanmaku.Core.GameIdentity;

// External metadata describes an installation/work, never a confirmed story namespace.
public sealed record GameLibraryEntry(string ProviderId, string SourceGameId, string ExecutablePath,
    string DisplayName, string? WorkId = null, string? VndbId = null, string? BangumiId = null,
    string? LaunchPath = null, string? LaunchMethod = null);
