using GalDanmaku.Core;
using GalDanmaku.Storage;
using Microsoft.Data.Sqlite;

namespace GalDanmaku.Tests;

public sealed class LocalLikeRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GalDanmaku-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "danmaku.db");

    [Fact]
    public async Task VersionOneMigrationPreservesOriginalContentAndRepeatedOpenIsIdempotent()
    {
        StoredDanmaku original = LocalPlaybackTests.Comment(1234, text: "迁移原文🌸 'quoted'", color: "#80D8FF", fontSize: 42);
        using (SqliteConnection connection = Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE danmaku (
                    id TEXT PRIMARY KEY NOT NULL, game_id TEXT NOT NULL, node_id TEXT NOT NULL,
                    text TEXT NOT NULL CHECK(length(text) BETWEEN 1 AND 500),
                    type INTEGER NOT NULL CHECK(type = 0), color TEXT NOT NULL,
                    font_size REAL NOT NULL CHECK(font_size BETWEEN 12 AND 64),
                    offset_ms INTEGER NOT NULL CHECK(offset_ms >= 0), created_utc_ticks INTEGER NOT NULL
                );
                CREATE INDEX ix_danmaku_game_node_offset ON danmaku(game_id, node_id, offset_ms, created_utc_ticks, id);
                INSERT INTO danmaku VALUES ($id, $game, $node, $text, 0, $color, $font, $offset, $created);
                PRAGMA user_version = 1;
                """;
            command.Parameters.AddWithValue("$id", original.Id.ToString("D"));
            command.Parameters.AddWithValue("$game", original.GameId);
            command.Parameters.AddWithValue("$node", original.NodeId);
            command.Parameters.AddWithValue("$text", original.Text);
            command.Parameters.AddWithValue("$color", original.Color);
            command.Parameters.AddWithValue("$font", original.FontSize);
            command.Parameters.AddWithValue("$offset", original.OffsetMilliseconds);
            command.Parameters.AddWithValue("$created", original.CreatedUtc.UtcTicks);
            command.ExecuteNonQuery();
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var reopened = new SqliteCommentRepository(DatabasePath);
            Assert.Equal(original, Assert.Single(await reopened.LoadAsync(original.GameId, original.NodeId, TestContext.Current.CancellationToken)));
        }

        using SqliteConnection verify = Open();
        using SqliteCommand query = verify.CreateCommand();
        query.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, query.ExecuteScalar());
        query.CommandText = "SELECT local_liked FROM danmaku WHERE id=$id";
        query.Parameters.AddWithValue("$id", original.Id.ToString("D"));
        Assert.Equal(0L, query.ExecuteScalar());
        query.CommandText = "SELECT COUNT(*) FROM pragma_table_info('danmaku') WHERE name='local_liked'";
        Assert.Equal(1L, query.ExecuteScalar());
        query.CommandText = "UPDATE danmaku SET local_liked=2";
        Assert.Throws<SqliteException>(() => query.ExecuteNonQuery());
    }

    [Fact]
    public async Task RepeatedSetAndUnsetPersistWithoutAffectingSameTextWithAnotherId()
    {
        var repository = new SqliteCommentRepository(DatabasePath);
        ILocalLikeRepository likes = repository;
        StoredDanmaku first = LocalPlaybackTests.Comment(text: "same text");
        StoredDanmaku second = LocalPlaybackTests.Comment(text: first.Text);
        await repository.SaveAsync(first, TestContext.Current.CancellationToken);
        await repository.SaveAsync(second, TestContext.Current.CancellationToken);
        for (int attempt = 0; attempt < 2; attempt++)
            await likes.SetLocalLikedAsync(first.GameId, first.NodeId, first.Id, true, TestContext.Current.CancellationToken);

        ILocalLikeRepository reopened = new SqliteCommentRepository(DatabasePath);
        Assert.True(await reopened.GetLocalLikedAsync(first.GameId, first.NodeId, first.Id, TestContext.Current.CancellationToken));
        Assert.False(await reopened.GetLocalLikedAsync(second.GameId, second.NodeId, second.Id, TestContext.Current.CancellationToken));
        for (int attempt = 0; attempt < 2; attempt++)
            await reopened.SetLocalLikedAsync(first.GameId, first.NodeId, first.Id, false, TestContext.Current.CancellationToken);

        ILocalLikeRepository afterUnlike = new SqliteCommentRepository(DatabasePath);
        Assert.False(await afterUnlike.GetLocalLikedAsync(first.GameId, first.NodeId, first.Id, TestContext.Current.CancellationToken));
        Assert.Equal(2, (await repository.LoadAsync(first.GameId, first.NodeId, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task WrongScopeOrUnknownIdentityFailsWithoutChangingExistingLike()
    {
        var repository = new SqliteCommentRepository(DatabasePath);
        StoredDanmaku original = LocalPlaybackTests.Comment();
        await repository.SaveAsync(original, TestContext.Current.CancellationToken);
        await repository.SetLocalLikedAsync(original.GameId, original.NodeId, original.Id, true, TestContext.Current.CancellationToken);
        foreach ((string game, string node, Guid id) in new[]
        {
            ("other-game", original.NodeId, original.Id),
            (original.GameId, "other-node", original.Id),
            (original.GameId, original.NodeId, Guid.NewGuid()),
        })
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.SetLocalLikedAsync(game, node, id, false, TestContext.Current.CancellationToken));
            Assert.Contains("弹幕", error.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.GetLocalLikedAsync(game, node, id, TestContext.Current.CancellationToken));
        }

        Assert.True(await repository.GetLocalLikedAsync(original.GameId, original.NodeId, original.Id, TestContext.Current.CancellationToken));
        Assert.Equal(original, Assert.Single(await repository.LoadAsync(original.GameId, original.NodeId, TestContext.Current.CancellationToken)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
