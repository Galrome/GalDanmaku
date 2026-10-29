using GalDanmaku.Core;
using GalDanmaku.Core.Story;
using GalDanmaku.Core.Text;

namespace GalDanmaku.Tests;

public sealed class NodeBindingCoordinatorTests
{
    private const string Game = "mock-game";

    [Fact]
    public async Task ConfirmedLoadsAndActivatesThenUnknownImmediatelyInvalidates()
    {
        var repository = new FakeRepository();
        using var session = new LocalCommentSession(repository);
        using var coordinator = new NodeBindingCoordinator(session, Game);
        var states = new List<NodeBindingState>();
        coordinator.StateChanged += (_, _) => states.Add(coordinator.State);

        coordinator.OnLocationChanged(Confirmed("node-a"), 1);
        await Until(() => coordinator.State == NodeBindingState.Active);
        Assert.Equal("node-a", coordinator.BoundNodeId);
        Assert.True(coordinator.CanSend);
        Assert.True(session.Playback.IsPlaying);

        coordinator.OnLocationChanged(Unknown(), 1);
        Assert.Equal(NodeBindingState.Idle, coordinator.State);
        Assert.Null(coordinator.BoundNodeId);
        Assert.False(coordinator.CanSend);
        Assert.False(session.IsLoaded);
        Assert.False(session.Playback.IsPlaying);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SendAsync("x", "#FFFFFF", 28, TestContext.Current.CancellationToken));
        Assert.Empty(repository.Saved);
        Assert.Equal(NodeBindingState.Idle, states[^1]);
    }

    [Fact]
    public async Task StaleLoadResultIsDiscardedWhenLocationMovesToAnotherNode()
    {
        var repository = new FakeRepository { Manual = true };
        using var session = new LocalCommentSession(repository);
        using var coordinator = new NodeBindingCoordinator(session, Game);

        coordinator.OnLocationChanged(Confirmed("node-a"), 1);
        coordinator.OnLocationChanged(Confirmed("node-b"), 2);
        Assert.Equal(NodeBindingState.Loading, coordinator.State);
        Assert.Single(repository.Loads);

        // The old repository ignores cancellation and finishes late with node-a data.
        repository.Loads[0].Completion.SetResult([FakeRepository.Comment("node-a")]);
        await Until(() => repository.Loads.Count == 2);
        Assert.Equal(NodeBindingState.Loading, coordinator.State);
        Assert.Equal("node-b", repository.Loads[1].NodeId);
        repository.Loads[1].Completion.SetResult([]);
        await Until(() => coordinator.State == NodeBindingState.Active);

        Assert.Equal("node-b", coordinator.BoundNodeId);
        Assert.Equal("node-b", session.NodeId);
        Assert.Equal(0, session.Playback.SavedCount);
    }

    [Fact]
    public async Task ResetMakesLateLoadCompletionHarmless()
    {
        var repository = new FakeRepository { Manual = true };
        using var session = new LocalCommentSession(repository);
        using var coordinator = new NodeBindingCoordinator(session, Game);
        int events = 0;
        coordinator.OnLocationChanged(Confirmed("node-a"), 1);
        coordinator.StateChanged += (_, _) => events++;

        coordinator.Reset();
        Assert.Equal(NodeBindingState.Idle, coordinator.State);
        int afterReset = events;
        repository.Loads[0].Completion.SetResult([FakeRepository.Comment("node-a")]);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(NodeBindingState.Idle, coordinator.State);
        Assert.Null(coordinator.BoundNodeId);
        Assert.False(coordinator.CanSend);
        Assert.False(session.IsLoaded);
        Assert.False(session.IsBusy);
        Assert.Equal(afterReset, events);
        Assert.Equal(0, session.Playback.SavedCount);
    }

    [Fact]
    public async Task LoadFailureReportsErrorAndNextConfirmationRetries()
    {
        var repository = new FakeRepository { Manual = true };
        using var session = new LocalCommentSession(repository);
        using var coordinator = new NodeBindingCoordinator(session, Game);

        coordinator.OnLocationChanged(Confirmed("node-a"), 1);
        repository.Loads[0].Completion.SetException(new IOException("read failed"));
        await Until(() => coordinator.State == NodeBindingState.Failed);
        Assert.Contains("read failed", coordinator.LastError);
        Assert.False(coordinator.CanSend);
        Assert.False(session.Playback.IsPlaying);

        coordinator.OnLocationChanged(Confirmed("node-a"), 2);
        await Until(() => repository.Loads.Count == 2);
        repository.Loads[1].Completion.SetResult([]);
        await Until(() => coordinator.State == NodeBindingState.Active);
        Assert.Null(coordinator.LastError);
    }

    [Fact]
    public async Task NodeChangeDuringSendKeepsSavedCommentOutOfNewNodePlayback()
    {
        var repository = new FakeRepository { ManualSave = true };
        using var session = new LocalCommentSession(repository);
        using var coordinator = new NodeBindingCoordinator(session, Game);
        coordinator.OnLocationChanged(Confirmed("node-a"), 1);
        await Until(() => coordinator.State == NodeBindingState.Active);

        Task<StoredDanmaku> send = coordinator.SendAsync("hello", "#FFFFFF", 28, TestContext.Current.CancellationToken);
        coordinator.OnLocationChanged(Confirmed("node-b"), 2);
        Assert.False(coordinator.CanSend);
        Assert.Equal(NodeBindingState.Loading, coordinator.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SendAsync("late", "#FFFFFF", 28, TestContext.Current.CancellationToken));
        Assert.Single(repository.Loads);

        repository.SaveCompletion.SetResult();
        StoredDanmaku saved = await send;
        Assert.Equal("node-a", saved.NodeId);
        await Until(() => coordinator.State == NodeBindingState.Active);

        Assert.Equal("node-b", coordinator.BoundNodeId);
        Assert.Equal(0, session.Playback.SavedCount);
        Assert.Equal(2, repository.Loads.Count);
        Assert.Equal("node-b", repository.Loads[1].NodeId);
        Assert.Single(repository.Saved);
    }

    private static StoryLocation Confirmed(string nodeId)
    {
        var node = new StoryNode(nodeId, "テキスト。", []);
        return new(StoryLocationStatus.Confirmed, node, [node], "confirmed");
    }

    private static StoryLocation Unknown() => new(StoryLocationStatus.Unknown, null, [], "unknown");

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "等待条件超时。");
    }

    private sealed class FakeRepository : ICommentRepository
    {
        public bool Manual { get; init; }
        public bool ManualSave { get; init; }
        public List<PendingLoad> Loads { get; } = [];
        public List<StoredDanmaku> Saved { get; } = [];
        public TaskCompletionSource SaveCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<StoredDanmaku>> LoadAsync(string gameId, string nodeId, CancellationToken cancellationToken = default)
        {
            var load = new PendingLoad(nodeId);
            Loads.Add(load);
            if (!Manual)
            {
                load.Completion.SetResult([]);
            }

            return load.Completion.Task;
        }

        public Task SaveAsync(StoredDanmaku comment, CancellationToken cancellationToken = default)
        {
            Saved.Add(comment);
            return ManualSave ? SaveCompletion.Task : Task.CompletedTask;
        }

        public static StoredDanmaku Comment(string nodeId) => LocalPlaybackTests.Comment(node: nodeId, game: Game);
    }

    private sealed record PendingLoad(string NodeId)
    {
        public TaskCompletionSource<IReadOnlyList<StoredDanmaku>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
