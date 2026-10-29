using System.Text.Json;
using GalDanmaku.Core.GameIdentity;

namespace GalDanmaku.Integrations.LunaTranslator;

public sealed class LunaGameLibraryProvider(string path) : IGameLibraryProvider
{
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private IReadOnlyList<GameLibraryEntry> _snapshot = [];

    public string ProviderId => "lunatranslator";
    public string Status { get; private set; } = "尚未读取 Luna 游戏库。";
    public bool HasValidSnapshot { get; private set; }

    public async Task<IReadOnlyList<GameLibraryEntry>> GetGamesAsync(CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Status = "未配置 Luna 游戏库目录。";
                return _snapshot;
            }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string filePath = LunaGameDataReader.ResolveFilePath(path);
                    LunaGameDataSnapshot data = await LunaGameDataReader.ReadAsync(filePath, cancellationToken).ConfigureAwait(false);
                    _snapshot = Array.AsReadOnly(data.Games.Select(game => new GameLibraryEntry(ProviderId, game.Uid, game.GamePath, game.Title,
                        game.VndbId is null ? null : "vndb:" + game.VndbId,
                        game.VndbId, game.BangumiId, game.LaunchPath, game.LaunchMethod)).ToArray());
                    HasValidSnapshot = true;
                    Status = data.SkippedRecords == 0 ? $"Luna 游戏库已读取：{_snapshot.Count} 条记录。"
                        : $"Luna 游戏库已读取：{_snapshot.Count} 条记录，跳过 {data.SkippedRecords} 条异常记录。";
                    return _snapshot;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                    or ArgumentException or NotSupportedException)
                {
                    if (attempt < 2)
                    {
                        await Task.Delay(100 * (attempt + 1), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    // Report a useful category without echoing arbitrary private file contents or paths.
                    string reason = error switch
                    {
                        FileNotFoundException or DirectoryNotFoundException => "未找到 Luna 游戏库文件",
                        UnauthorizedAccessException => "没有权限读取 Luna 游戏库",
                        JsonException => "Luna 游戏库 JSON 无效或嵌套过深",
                        InvalidDataException => error.Message,
                        ArgumentException or NotSupportedException => "Luna 游戏库路径无效",
                        _ => "Luna 游戏库暂时无法读取"
                    };
                    Status = HasValidSnapshot ? $"{reason}；继续使用上次有效快照。" : $"{reason}；可继续使用本地游戏映射。";
                }
            }
            return _snapshot;
        }
        finally { _readGate.Release(); }
    }
}
