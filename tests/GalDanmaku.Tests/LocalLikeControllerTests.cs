using GalDanmaku.Core;

namespace GalDanmaku.Tests;

public sealed class LocalLikeControllerTests
{
    [Fact]
    public async Task FailedReadStaysUnknownUntilSuccessfulRetry()
    {
        var repository = new ControlledLikes { Read = (_, _, _, _) => throw new IOException("unavailable") };
        await using var controller = new LocalLikeController(repository);
        await controller.SelectAsync(Comment("node"));
        Assert.Null(controller.IsLiked);
        Assert.False(controller.CanToggle);
        Assert.True(controller.CanRetry);
        repository.Read = (_, _, _, _) => Task.FromResult(true);
        await controller.RetryAsync();
        Assert.True(controller.IsLiked);
        Assert.True(controller.CanToggle);
        Assert.Null(controller.Error);
    }

    [Fact]
    public async Task SaveFailureRetainsConfirmedStateAndCanRetry()
    {
        var repository = new ControlledLikes { Write = (_, _, _, _, _) => throw new IOException("locked") };
        await using var controller = new LocalLikeController(repository);
        await controller.SelectAsync(Comment("node"));
        await controller.ToggleAsync();
        Assert.False(controller.IsLiked);
        Assert.True(controller.CanRetry);
        repository.Write = (_, _, _, _, _) => Task.CompletedTask;
        await controller.RetryAsync();
        Assert.True(controller.IsLiked);
        Assert.Null(controller.Error);
    }

    private static StoredDanmaku Comment(string node) => new(Guid.NewGuid(), "game", node, "comment",
        DanmakuType.Scrolling, "#FFFFFF", 24, 0, DateTimeOffset.UtcNow);

    private sealed class ControlledLikes : ILocalLikeRepository
    {
        public Func<string, string, Guid, CancellationToken, Task<bool>> Read { get; set; } = (_, _, _, _) => Task.FromResult(false);
        public Func<string, string, Guid, bool, CancellationToken, Task> Write { get; set; } = (_, _, _, _, _) => Task.CompletedTask;
        public Task<bool> GetLocalLikedAsync(string game, string node, Guid id, CancellationToken token = default) => Read(game, node, id, token);
        public Task SetLocalLikedAsync(string game, string node, Guid id, bool liked, CancellationToken token = default) => Write(game, node, id, liked, token);
    }
}
