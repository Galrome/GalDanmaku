using GalDanmaku.Core;
using GalDanmaku.Storage;
using Microsoft.Data.Sqlite;

namespace GalDanmaku.Tests;

public sealed class SqliteCommentRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GalDanmaku-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "danmaku.db");

    [Fact]
    public async Task RestartRoundTripsUnicodeQuotesAndEveryStoredField()
    {
        StoredDanmaku comment = LocalPlaybackTests.Comment(1234, text: "你好🌸 '); DROP TABLE danmaku; --", fontSize: 42, color: "#80D8FF");
        await new SqliteCommentRepository(DatabasePath).SaveAsync(comment, TestContext.Current.CancellationToken);
        var reopened = new SqliteCommentRepository(DatabasePath);
        Assert.Equal(comment, Assert.Single(await reopened.LoadAsync("mock-game", "node-a", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task QueriesIsolateBothNodeAndGame()
    {
        var repository = new SqliteCommentRepository(DatabasePath);
        StoredDanmaku a = LocalPlaybackTests.Comment();
        await repository.SaveAsync(a, TestContext.Current.CancellationToken);
        await repository.SaveAsync(LocalPlaybackTests.Comment(node: "node-b"), TestContext.Current.CancellationToken);
        await repository.SaveAsync(LocalPlaybackTests.Comment(game: "another-game"), TestContext.Current.CancellationToken);
        Assert.Equal(a, Assert.Single(await repository.LoadAsync("mock-game", "node-a", TestContext.Current.CancellationToken)));
        Assert.Empty(await repository.LoadAsync("' OR 1=1 --", "node-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SchemaBootstrapIsIdempotentAndCreatesNodeOffsetIndex()
    {
        await new SqliteCommentRepository(DatabasePath).LoadAsync("mock-game", "node-a", TestContext.Current.CancellationToken);
        await new SqliteCommentRepository(DatabasePath).LoadAsync("mock-game", "node-a", TestContext.Current.CancellationToken);
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM pragma_index_info('ix_danmaku_game_node_offset') WHERE name IN ('game_id', 'node_id', 'offset_ms')";
        Assert.Equal(3L, command.ExecuteScalar());
    }

    [Fact]
    public async Task FutureSchemaIsRefusedWithoutChangingDataOrVersion()
    {
        using (SqliteConnection connection = Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version=2; CREATE TABLE future_data(value TEXT); INSERT INTO future_data VALUES('keep me')";
            command.ExecuteNonQuery();
        }

        var repository = new SqliteCommentRepository(DatabasePath);
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.LoadAsync("mock-game", "node-a", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.SaveAsync(LocalPlaybackTests.Comment(), TestContext.Current.CancellationToken));
        using SqliteConnection verify = Open();
        using SqliteCommand query = verify.CreateCommand();
        query.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, query.ExecuteScalar());
        query.CommandText = "SELECT value FROM future_data";
        Assert.Equal("keep me", query.ExecuteScalar());
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='danmaku'";
        Assert.Equal(0L, query.ExecuteScalar());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
