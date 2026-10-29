using GalDanmaku.Core;
using Microsoft.Data.Sqlite;

namespace GalDanmaku.Storage;

public sealed class SqliteCommentRepository : ICommentRepository
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
    }

    public Task SaveAsync(StoredDanmaku comment, CancellationToken cancellationToken = default)
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
    }

    private static void InitializeSchema(SqliteConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        long version = (long)command.ExecuteScalar()!;
        if (version is not (0 or 1))
        {
            throw new NotSupportedException($"数据库版本 {version} 不受支持；本程序支持版本 1。原数据库未修改。");
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

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }
}
