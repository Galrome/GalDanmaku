using GalDanmaku.Core;

namespace GalDanmaku.Tests;

public sealed class LocalCommentSessionTests
{
    [Fact]
    public async Task InvalidMultilineDraftDoesNotSaveOrQueueAndSessionRemainsAvailable()
    {
        var repository = new ControlledRepository();
        repository.SaveCompletion.SetResult();
        using var session = new LocalCommentSession(repository);
        await session.LoadAsync("mock-game", "node-a", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.SendAsync("first\u2028second\u2029third", "#FFFFFF", 28, TestContext.Current.CancellationToken));
        Assert.Empty(repository.SaveRequests);
        Assert.Equal(0, session.Playback.SavedCount);
        Assert.False(session.IsBusy);
        Assert.True(session.IsLoaded);
        StoredDanmaku valid = await session.SendAsync("single line", "#FFFFFF", 28, TestContext.Current.CancellationToken);
        Assert.Equal("single line", valid.Text);
        Assert.Equal(1, session.Playback.SavedCount);
    }

    [Fact]
    public async Task SendCapturesNodeTimeAndStyleBeforeAwaitAndQueuesAfterSave()
    {
        var repository = new ControlledRepository();
        using var session = new LocalCommentSession(repository);
        await session.LoadAsync("mock-game", "node-a", TestContext.Current.CancellationToken);
        session.Playback.IsPlaying = true;
        session.Playback.Advance(1.25, _ => true);
        Task<StoredDanmaku> send = session.SendAsync("新弹幕🌸", "#80D8FF", 48, TestContext.Current.CancellationToken);
        Assert.True(session.IsBusy);
        Assert.Equal(0, session.Playback.SavedCount);
        session.Playback.Advance(1, _ => true);
        repository.SaveCompletion.SetResult();
        StoredDanmaku saved = await send;
        Assert.Equal(1250, saved.OffsetMilliseconds);
        Assert.Equal("node-a", saved.NodeId);
        Assert.Equal("mock-game", saved.GameId);
        Assert.Equal(48, saved.FontSize);
        Assert.Equal("#80D8FF", saved.Color);
        Assert.Equal(TimeSpan.Zero, saved.CreatedUtc.Offset);
        Assert.Equal(1, session.Playback.SavedCount);
        var emitted = new List<StoredDanmaku>();
        session.Playback.Advance(0, item => { emitted.Add(item); return true; });
        session.Playback.Advance(1, item => { emitted.Add(item); return true; });
        Assert.Equal(saved, Assert.Single(emitted));
    }

    [Fact]
    public async Task ConflictingNodeSwitchDuringSaveIsRejectedAndCannotMisapplyResult()
    {
        var repository = new ControlledRepository();
        using var session = new LocalCommentSession(repository);
        await session.LoadAsync("mock-game", "node-a", TestContext.Current.CancellationToken);
        Task<StoredDanmaku> send = session.SendAsync("A", "#FFFFFF", 28, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.LoadAsync("mock-game", "node-b", TestContext.Current.CancellationToken));
        Assert.Equal("node-a", session.NodeId);
        repository.SaveCompletion.SetResult();
        await send;
        await session.LoadAsync("mock-game", "node-b", TestContext.Current.CancellationToken);
        Assert.Equal("node-b", session.NodeId);
        Assert.Equal(0, session.Playback.SavedCount);
        Assert.Equal(0, session.Playback.PendingCount);
    }

    [Fact]
    public async Task FailedSaveDoesNotQueueOrClaimStoredCommentAndCanBeRetried()
    {
        var repository = new ControlledRepository();
        using var session = new LocalCommentSession(repository);
        await session.LoadAsync("mock-game", "node-a", TestContext.Current.CancellationToken);
        Task<StoredDanmaku> send = session.SendAsync("keep input", "#FFFFFF", 28, TestContext.Current.CancellationToken);
        repository.SaveCompletion.SetException(new IOException("disk full"));
        await Assert.ThrowsAsync<IOException>(() => send);
        Assert.Equal(0, session.Playback.SavedCount);
        Assert.False(session.IsBusy);
        Assert.True(session.IsLoaded);
    }

    private sealed class ControlledRepository : ICommentRepository
    {
        public bool DelayLoad { get; set; }
        public TaskCompletionSource<IReadOnlyList<StoredDanmaku>> LoadCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken OperationToken { get; private set; }
        public List<StoredDanmaku> SaveRequests { get; } = [];

        public Task<IReadOnlyList<StoredDanmaku>> LoadAsync(string gameId, string nodeId, CancellationToken cancellationToken = default)
        {
            OperationToken = cancellationToken;
            return DelayLoad ? LoadCompletion.Task : Task.FromResult<IReadOnlyList<StoredDanmaku>>([]);
        }

        public Task SaveAsync(StoredDanmaku comment, CancellationToken cancellationToken = default)
        {
            OperationToken = cancellationToken;
            SaveRequests.Add(comment);
            return SaveCompletion.Task;
        }
    }
}
