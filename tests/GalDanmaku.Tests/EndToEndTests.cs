using System.Diagnostics;
using GalDanmaku.Core;
using GalDanmaku.Core.GamePacks;
using GalDanmaku.Core.Story;
using GalDanmaku.Core.Text;
using GalDanmaku.Storage;

namespace GalDanmaku.Tests;

// Headless end-to-end checks of the main chain, wired exactly like the App layer:
// pack.CleanText -> DialogueProcessor -> StoryLocator -> NodeBindingCoordinator -> LocalCommentSession -> SQLite.
public sealed class EndToEndTests : IDisposable
{
    private const string NodeIntro = "demo:a-intro";
    private const string NodeRepeatA = "demo:a-repeat";
    private const string NodeRepeatB = "demo:b-repeat";
    private const string IntroA = "朝の庭で、青い花を見つけた。";
    private const string IntroB = "夕方の坂で、赤い傘を拾った。";
    private const string Repeat = "また会えたね。";
    private const string Repeat2 = "少し歩こう。";
    private const string EndA = "青い花を窓辺に飾ろう。";
    private const string EndB = "赤い傘を玄関に置こう。";
    private const string Converge = "明日は、きっと晴れる。";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GalDanmaku-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public EndToEndTests(ITestOutputHelper output) => _output = output;

    private string DatabasePath => Path.Combine(_directory, "e2e.db");

    // A fresh repository instance per call simulates an application restart on the same file.
    private SqliteCommentRepository NewRepository() => new(DatabasePath);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best effort: the directory lives in the system temp folder.
        }
    }

    [Fact]
    public async Task NormalRoute_ConfirmsNodesInOrder_PersistsAndReloadsAfterRestart()
    {
        LoadedGamePack loaded = await LoadPackAsync("synthetic-a");
        var visited = new List<string>();
        bool sent = false;

        using (var pipeline = new Pipeline(loaded, NewRepository()))
        {
            foreach (string frame in loaded.Pack.TextSource.MockScript)
            {
                StoryLocation location = await pipeline.FeedAsync(frame);
                if (location.Status != StoryLocationStatus.Confirmed)
                {
                    Assert.False(pipeline.Coordinator.CanSend);
                    Assert.Null(pipeline.Coordinator.BoundNodeId);
                }

                if (pipeline.Coordinator.State == NodeBindingState.Active && pipeline.Coordinator.BoundNodeId != visited.LastOrDefault())
                {
                    visited.Add(pipeline.Coordinator.BoundNodeId!);
                }

                if (pipeline.Coordinator.BoundNodeId == NodeIntro && !sent)
                {
                    sent = true;
                    Assert.True(pipeline.Coordinator.CanSend);
                    pipeline.Session.Playback.Advance(0.5, _ => true);
                    await pipeline.Coordinator.SendAsync("first", "#ff8800", 28, TestContext.Current.CancellationToken);
                    pipeline.Session.Playback.Advance(1.0, _ => true);
                    StoredDanmaku second = await pipeline.Coordinator.SendAsync("second", "#00ff00", 24, TestContext.Current.CancellationToken);
                    Assert.Equal(1500, second.OffsetMilliseconds);
                }
            }
        }

        Assert.Equal([NodeIntro, NodeRepeatA, "demo:a-repeat2", "demo:a-end", "demo:converge"], visited);
        Assert.True(sent);

        // Restart: brand-new repository, session, coordinator, locator and processor.
        using var restarted = new Pipeline(loaded, NewRepository());
        await restarted.FeedAsync(IntroA);
        Assert.Equal(NodeIntro, restarted.Coordinator.BoundNodeId);
        Assert.Equal(2, restarted.Session.Playback.SavedCount);
        Assert.True(restarted.Session.Playback.IsPlaying);
        Assert.Empty(restarted.Drain(0.25));
        Assert.Equal(["first"], restarted.Drain(0.25));
        Assert.Empty(restarted.Drain(0.5));
        Assert.Equal(["second"], restarted.Drain(0.5));
        Assert.Empty(restarted.Drain(5));

        IReadOnlyList<StoredDanmaku> persisted = await NewRepository().LoadAsync(loaded.Pack.GameId, NodeIntro, TestContext.Current.CancellationToken);
        Assert.Equal(["#FF8800", "#00FF00"], persisted.Select(comment => comment.Color));
    }

    private static async Task<LoadedGamePack> LoadPackAsync(string name)
    {
        return await GamePackLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gamepacks", name), TestContext.Current.CancellationToken);
    }

    private async Task<string[]> TextsAsync(string gameId, string nodeId) =>
        (await NewRepository().LoadAsync(gameId, nodeId, TestContext.Current.CancellationToken)).Select(comment => comment.Text).ToArray();

    // Mirrors the App wiring from TextSourceViewModel/StoryLocatorViewModel/GamePackViewModel without any UI.
    private sealed class Pipeline : IDisposable
    {
        private readonly LoadedGamePack _loaded;
        private readonly DialogueProcessor _processor = new(TimeSpan.FromMilliseconds(500));
        private readonly StoryLocator _locator;
        private readonly bool _ownsSession;
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private string _previousRaw = "";

        public Pipeline(LoadedGamePack loaded, ICommentRepository repository, LocalCommentSession? session = null)
        {
            _loaded = loaded;
            _locator = new StoryLocator(loaded.Index);
            _ownsSession = session is null;
            Session = session ?? new LocalCommentSession(repository);
            Coordinator = new NodeBindingCoordinator(Session, loaded.Pack.GameId);
        }

        public LocalCommentSession Session { get; }
        public NodeBindingCoordinator Coordinator { get; }
        public GamePack Pack => _loaded.Pack;

        public StoryLocation Feed(string raw)
        {
            // Typewriter-style growth arrives quickly (merged as an update); anything else is a new dialogue.
            bool typing = _previousRaw.Length > 0 && raw.StartsWith(_previousRaw, StringComparison.Ordinal);
            _now += typing ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(1);
            _previousRaw = raw;
            TextProcessResult result = _processor.Process(_loaded.Pack.CleanText(raw), _now);
            if (result.Event is not { } dialogue) return _locator.Current;
            StoryLocation location = _locator.Process(dialogue);
            Coordinator.OnLocationChanged(location, _locator.ConfirmationVersion);
            return location;
        }

        public async Task<StoryLocation> FeedAsync(string raw)
        {
            StoryLocation location = Feed(raw);
            await SettleAsync();
            return location;
        }

        public async Task SettleAsync()
        {
            var timeout = Stopwatch.StartNew();
            while (Coordinator.State == NodeBindingState.Loading && timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(2, TestContext.Current.CancellationToken);
            }

            Assert.NotEqual(NodeBindingState.Loading, Coordinator.State);
            Assert.NotEqual(NodeBindingState.Failed, Coordinator.State);
        }

        public List<string> Drain(double seconds)
        {
            var texts = new List<string>();
            Session.Playback.Advance(seconds, comment =>
            {
                texts.Add(comment.Text);
                return true;
            });
            return texts;
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            if (_ownsSession) Session.Dispose();
        }
    }

    // Real SQLite underneath; gates let a test freeze a save before or after the commit.
    private sealed class GatedRepository(ICommentRepository inner) : ICommentRepository
    {
        public TaskCompletionSource? BeforeSave { get; init; }
        public TaskCompletionSource? AfterSave { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<StoredDanmaku>> LoadAsync(string gameId, string nodeId, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(gameId, nodeId, cancellationToken);

        public async Task SaveAsync(StoredDanmaku comment, CancellationToken cancellationToken = default)
        {
            if (BeforeSave is { } before)
            {
                Entered.TrySetResult();
                await before.Task.WaitAsync(cancellationToken);
            }

            await inner.SaveAsync(comment, cancellationToken);
            if (AfterSave is { } after)
            {
                Committed.TrySetResult();
                await after.Task;
            }
        }
    }
}
