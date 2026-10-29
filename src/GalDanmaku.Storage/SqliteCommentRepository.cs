using System.Diagnostics;
using GalDanmaku.Core.Diagnostics;
using GalDanmaku.Core;
using Microsoft.Data.Sqlite;

namespace GalDanmaku.Storage;

public sealed class SqliteCommentRepository : ICommentRepository, ILocalLikeRepository
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteCommentRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
            DefaultTimeout = 2,
        }.ToString();
    }

    public Task<IReadOnlyList<StoredDanmaku>> LoadAsync(string gameId, string nodeId, CancellationToken cancellationToken = default)
    {
        return LogOperationAsync<IReadOnlyList<StoredDanmaku>>("SqliteQuery", new LogContext(GameId: gameId, NodeId: nodeId, OperationId: Guid.NewGuid().ToString("N")), () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
            // Microsoft.Data.Sqlite executes synchronously, including its Async APIs.
            return Task.Run<IReadOnlyList<StoredDanmaku>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                InitializeSchema(connection, cancellationToken);
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, game_id, node_id, text, type, color, font_size, offset_ms, created_utc_ticks
                    FROM danmaku WHERE game_id = $game AND node_id = $node
                    ORDER BY offset_ms, created_utc_ticks, id
                    """;
                command.Parameters.AddWithValue("$game", gameId);
                command.Parameters.AddWithValue("$node", nodeId);
                using SqliteDataReader reader = command.ExecuteReader();
                var comments = new List<StoredDanmaku>();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    comments.Add(new StoredDanmaku(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), (DanmakuType)reader.GetInt32(4), reader.GetString(5), reader.GetDouble(6),
                        reader.GetInt64(7), new DateTimeOffset(reader.GetInt64(8), TimeSpan.Zero)));
                }

                cancellationToken.ThrowIfCancellationRequested();
                return comments;
            }, cancellationToken);
        }, static comments => comments.Count);
    }

    public Task SaveAsync(StoredDanmaku comment, CancellationToken cancellationToken = default)
    {
        return LogOperationAsync("SqliteWrite", new LogContext(GameId: comment?.GameId, NodeId: comment?.NodeId, OperationId: Guid.NewGuid().ToString("N")), () =>
        {
            ArgumentNullException.ThrowIfNull(comment);
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                InitializeSchema(connection, cancellationToken);
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO danmaku (id, game_id, node_id, text, type, color, font_size, offset_ms, created_utc_ticks)
                    VALUES ($id, $game, $node, $text, $type, $color, $font, $offset, $created)
                    """;
                command.Parameters.AddWithValue("$id", comment.Id.ToString("D"));
                command.Parameters.AddWithValue("$game", comment.GameId);
                command.Parameters.AddWithValue("$node", comment.NodeId);
                command.Parameters.AddWithValue("$text", comment.Text);
                command.Parameters.AddWithValue("$type", (int)comment.Type);
                command.Parameters.AddWithValue("$color", comment.Color);
                command.Parameters.AddWithValue("$font", comment.FontSize);
                command.Parameters.AddWithValue("$offset", comment.OffsetMilliseconds);
                command.Parameters.AddWithValue("$created", comment.CreatedUtc.UtcTicks);
                cancellationToken.ThrowIfCancellationRequested();
                command.ExecuteNonQuery();
                cancellationToken.ThrowIfCancellationRequested();
                // Once commit succeeds, report success even if cancellation arrives at that boundary.
                transaction.Commit();
            }, cancellationToken);
        });
    }

    public Task<bool> GetLocalLikedAsync(string gameId, string nodeId, Guid commentId, CancellationToken cancellationToken = default)
    {
        return LogOperationAsync<bool>("SqliteLikeQuery", new LogContext(GameId: gameId, NodeId: nodeId, OperationId: Guid.NewGuid().ToString("N")), () =>
        {
            ValidateLocalLikeIdentity(gameId, nodeId, commentId);
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                InitializeSchema(connection, cancellationToken);
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT local_liked FROM danmaku WHERE id=$id AND game_id=$game AND node_id=$node";
                AddLocalLikeIdentity(command, gameId, nodeId, commentId);
                object? result = command.ExecuteScalar();
                cancellationToken.ThrowIfCancellationRequested();
                return result is long value ? value == 1 : throw MissingLocalLikeComment();
            }, cancellationToken);
        }, static _ => 1);
    }

    public Task SetLocalLikedAsync(string gameId, string nodeId, Guid commentId, bool liked, CancellationToken cancellationToken = default)
    {
        return LogOperationAsync("SqliteLikeUpsert", new LogContext(GameId: gameId, NodeId: nodeId, OperationId: Guid.NewGuid().ToString("N")), () =>
        {
            ValidateLocalLikeIdentity(gameId, nodeId, commentId);
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                InitializeSchema(connection, cancellationToken);
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE danmaku SET local_liked=$liked WHERE id=$id AND game_id=$game AND node_id=$node";
                AddLocalLikeIdentity(command, gameId, nodeId, commentId);
                command.Parameters.AddWithValue("$liked", liked ? 1 : 0);
                cancellationToken.ThrowIfCancellationRequested();
                int affectedRows = command.ExecuteNonQuery();
                cancellationToken.ThrowIfCancellationRequested();
                if (affectedRows != 1)
                {
                    throw MissingLocalLikeComment();
                }

                // Once commit succeeds, report success even if cancellation arrives at that boundary.
                transaction.Commit();
            }, cancellationToken);
        });
    }

    // The outer boundary keeps argument validation synchronous, as it was before instrumentation.
    // The observer also sees Task.Run tasks canceled before their delegate starts.
    private static Task<T> LogOperationAsync<T>(string name, LogContext context, Func<Task<T>> action, Func<T, int> count)
    {
        using var scope = RuntimeLog.BeginScope(context);
        long started = Stopwatch.GetTimestamp();
        RuntimeLog.Write(LogLevel.Debug, "SQLite", name + "Started");
        try { return ObserveAsync(action(), name, started, count); }
        catch (Exception exception)
        {
            LogFailure(name, started, exception);
            throw;
        }
    }

    private static Task LogOperationAsync(string name, LogContext context, Func<Task> action)
    {
        using var scope = RuntimeLog.BeginScope(context);
        long started = Stopwatch.GetTimestamp();
        RuntimeLog.Write(LogLevel.Debug, "SQLite", name + "Started");
        try { return ObserveAsync(action(), name, started); }
        catch (Exception exception)
        {
            LogFailure(name, started, exception);
            throw;
        }
    }

    private static async Task<T> ObserveAsync<T>(Task<T> operation, string name, long started, Func<T, int> count)
    {
        try
        {
            T result = await operation.ConfigureAwait(false);
            RuntimeLog.Write(LogLevel.Debug, "SQLite", name + "Succeeded", new { count = count(result), durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            return result;
        }
        catch (Exception exception)
        {
            LogFailure(name, started, exception);
            throw;
        }
    }

    private static async Task ObserveAsync(Task operation, string name, long started)
    {
        try
        {
            await operation.ConfigureAwait(false);
            RuntimeLog.Write(LogLevel.Debug, "SQLite", name + "Succeeded", new { count = 1, durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }
        catch (Exception exception)
        {
            LogFailure(name, started, exception);
            throw;
        }
    }

    private static void LogFailure(string name, long started, Exception exception) =>
        RuntimeLog.Write(exception is OperationCanceledException ? LogLevel.Debug : LogLevel.Error, "SQLite",
            name + (exception is OperationCanceledException ? "Cancelled" : "Failed"),
            new { durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }, exception);

    private static void ValidateLocalLikeIdentity(string gameId, string nodeId, Guid commentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (commentId == Guid.Empty)
        {
            throw new ArgumentException("弹幕 ID 不能为空。", nameof(commentId));
        }
    }

    private static void AddLocalLikeIdentity(SqliteCommand command, string gameId, string nodeId, Guid commentId)
    {
        command.Parameters.AddWithValue("$id", commentId.ToString("D"));
        command.Parameters.AddWithValue("$game", gameId);
        command.Parameters.AddWithValue("$node", nodeId);
    }

    private static InvalidOperationException MissingLocalLikeComment() =>
        new("当前游戏与节点中不存在这条原始弹幕，无法读取或设置本地点赞。");

    private static void InitializeSchema(SqliteConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        long version = (long)command.ExecuteScalar()!;
        if (version is not (0 or 1 or 2))
        {
            throw new NotSupportedException($"数据库版本 {version} 不受支持；本程序支持版本 2。原数据库未修改。");
        }

        if (version == 0)
        {
            command.CommandText = """
                CREATE TABLE danmaku (
                    id TEXT PRIMARY KEY NOT NULL,
                    game_id TEXT NOT NULL,
                    node_id TEXT NOT NULL,
                    text TEXT NOT NULL CHECK(length(text) BETWEEN 1 AND 500),
                    type INTEGER NOT NULL CHECK(type = 0),
                    color TEXT NOT NULL,
                    font_size REAL NOT NULL CHECK(font_size BETWEEN 12 AND 64),
                    offset_ms INTEGER NOT NULL CHECK(offset_ms >= 0),
                    created_utc_ticks INTEGER NOT NULL
                );
                CREATE INDEX ix_danmaku_game_node_offset ON danmaku(game_id, node_id, offset_ms, created_utc_ticks, id);
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 2)
        {
            command.CommandText = """
                ALTER TABLE danmaku ADD COLUMN local_liked INTEGER NOT NULL DEFAULT 0 CHECK(local_liked IN (0, 1));
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }
}
