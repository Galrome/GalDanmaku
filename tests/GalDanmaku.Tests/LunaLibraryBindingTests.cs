using System.Text.Json;
using GalDanmaku.App.ViewModels;
using GalDanmaku.Core;
using GalDanmaku.Core.GamePacks;
using GalDanmaku.Core.Text;

namespace GalDanmaku.Tests;

public sealed class LunaLibraryBindingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GalDanmaku-LunaBinding-" + Guid.NewGuid().ToString("N"));

    public LunaLibraryBindingTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ManualConfirmationRestoresWithoutLunaAndRejectsWrongGameId()
    {
        string packDirectory = MakePack("gd:story:one");
        string game = Path.Combine(_root, "game.exe");
        string library = await WriteLibraryAsync(game);
        using var session = new LocalCommentSession(new EmptyRepository());
        await using var text = NewTextSource();
        await using var story = new StoryLocatorViewModel(text, "unused.json");
        await using var packs = NewPacks(session, text, story);
        await packs.ConfigureLunaLibraryAsync(library);
        await packs.RefreshAsync();
        await packs.BindWindowAsync(game);
        Assert.Null(packs.ActivePack);
        Assert.Single(packs.IdentityCandidates);
        Assert.Contains("v60663", packs.IdentityDetails);
        Assert.True(packs.ConfirmAssociationCommand.CanExecute(null));
        await packs.ConfirmAssociationCommand.ExecuteAsync(null);
        Assert.Equal("gd:story:one", packs.ActivePack?.GameId);

        await packs.ClearWindowBindingAsync();
        File.Delete(library);
        // A fresh VM has no in-memory library snapshot and must restore the saved mapping alone.
        await using var restoredText = NewTextSource();
        await using var restoredStory = new StoryLocatorViewModel(restoredText, "unused.json");
        await using var restored = NewPacks(session, restoredText, restoredStory);
        Assert.Equal(library, restored.LunaLibraryDirectory);
        await restored.RefreshAsync();
        await restored.BindWindowAsync(game);
        Assert.Equal("gd:story:one", restored.ActivePack?.GameId);

        // Manifest and index change after catalog discovery: the required namespace still guards activation.
        MakePack("gd:story:wrong");
        await restored.LoadPackAsync(packDirectory);
        Assert.Null(restored.ActivePack);
        Assert.Contains("GameId", restored.Status);
        Assert.True(restoredText.IsAvailable);
    }

    [Fact]
    public async Task DuplicateCandidatesRequireSelectionAndRefreshDoesNotStopReception()
    {
        MakePack("gd:story:one");
        string game = Path.Combine(_root, "game.exe");
        string library = await WriteLibraryAsync(game, duplicate: true);
        using var session = new LocalCommentSession(new EmptyRepository());
        await using var text = NewTextSource();
        await using var story = new StoryLocatorViewModel(text, "unused.json");
        await using var packs = NewPacks(session, text, story);
        await packs.ConfigureLunaLibraryAsync(library);
        await packs.RefreshAsync();
        await packs.BindWindowAsync(game);
        Assert.Equal(2, packs.IdentityCandidates.Count);
        Assert.Null(packs.SelectedLibraryCandidate);
        Assert.False(packs.ConfirmAssociationCommand.CanExecute(null));
        Assert.Null(packs.ActivePack);

        Task reception = text.StartCommand.ExecuteAsync(null);
        Assert.True(text.IsRunning);
        await File.WriteAllTextAsync(library, "invalid", TestContext.Current.CancellationToken);
        await packs.RefreshLibraryCommand.ExecuteAsync(null);
        Assert.True(text.IsRunning);
        Assert.Equal(2, packs.IdentityCandidates.Count);
        Assert.Contains("快照", packs.LibraryStatus);
        await packs.ConfigureLunaLibraryAsync(Path.Combine(_root, "absent.json"));
        Assert.True(text.IsRunning);
        await text.StopAsync();
        await reception;
    }

    [Fact]
    public async Task LatePackLoadCannotActivateAfterAnotherWindowIsBound()
    {
        string directory = MakePack("gd:story:one");
        string game = Path.Combine(_root, "game.exe");
        string file = Path.Combine(_root, "game-mappings.local.json");
        await LocalGameMappings.SaveConfirmationAsync(file, file,
            new LocalGameMapping("local", "Game", "game.exe", game, "gd:story:one"), TestContext.Current.CancellationToken);
        LoadedGamePack loaded = await GamePackLoader.LoadAsync(directory, TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<LoadedGamePack>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new LocalCommentSession(new EmptyRepository());
        await using var text = NewTextSource();
        await using var story = new StoryLocatorViewModel(text, "unused.json");
        await using var packs = new GamePackViewModel(session, text, story,
            loader: (_, _) => { entered.SetResult(); return completion.Task; }, roots: [Path.Combine(_root, "packs")],
            mappingPath: file, identitySettingsDirectory: _root);
        Task old = packs.BindWindowAsync(game);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await packs.BindWindowAsync(Path.Combine(_root, "other.exe"));
        completion.SetResult(loaded);
        await old;
        Assert.Null(packs.ActivePack);
        Assert.Null(packs.Coordinator);
        Assert.True(text.IsAvailable);
    }

    [Fact]
    public async Task ReassociationRequiresExplicitStartAndCompareAndSwapPreservesUnrelatedMappings()
    {
        MakePack("gd:story:replacement");
        string game = Path.Combine(_root, "game.exe");
        string library = await WriteLibraryAsync(game);
        string mapping = Path.Combine(_root, "game-mappings.local.json");
        var legacy = new LocalGameMapping("local:legacy", "Legacy", "game.exe", game);
        var unrelated = new LocalGameMapping("local:other", "Other", "other.exe", Path.Combine(_root, "other.exe"));
        await File.WriteAllTextAsync(mapping, JsonSerializer.Serialize(new { version = 1, games = new[] { legacy, unrelated } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), TestContext.Current.CancellationToken);
        byte[] original = await File.ReadAllBytesAsync(mapping, TestContext.Current.CancellationToken);
        using var session = new LocalCommentSession(new EmptyRepository());
        await using var text = NewTextSource();
        await using var story = new StoryLocatorViewModel(text, "unused.json");
        await using var packs = NewPacks(session, text, story);
        await packs.ConfigureLunaLibraryAsync(library);
        await packs.RefreshAsync();
        await packs.BindWindowAsync(game);
        Assert.Contains("local:legacy", packs.IdentityDetails);
        Assert.Contains("v60663", packs.IdentityDetails);
        Assert.Single(packs.IdentityCandidates);
        Assert.False(packs.ConfirmAssociationCommand.CanExecute(null));

        Task reception = text.StartCommand.ExecuteAsync(null);
        packs.BeginReassociationCommand.Execute(null);
        Assert.True(text.IsRunning);
        Assert.True(packs.IsReassociating);
        packs.CancelReassociationCommand.Execute(null);
        Assert.False(packs.IsReassociating);
        Assert.True(text.IsRunning);
        Assert.Equal(original, await File.ReadAllBytesAsync(mapping, TestContext.Current.CancellationToken));

        packs.BeginReassociationCommand.Execute(null);
        Assert.True(packs.ConfirmAssociationCommand.CanExecute(null));
        Assert.Contains("local:legacy → gd:story:replacement", packs.AssociationNotice);
        await packs.ConfirmAssociationCommand.ExecuteAsync(null);
        await reception;
        Assert.Equal("gd:story:replacement", packs.ActivePack?.GameId);
        LocalGameMappings saved = await LocalGameMappings.LoadAsync(mapping, TestContext.Current.CancellationToken);
        Assert.Equal("gd:story:replacement", saved.MatchExact(game).Game?.RequiredGamePackId);
        Assert.Equal("one", saved.MatchExact(game).Game?.SourceGameId);
        Assert.Equal(unrelated, saved.MatchExact(unrelated.ExecutablePath).Game);
        byte[] confirmed = await File.ReadAllBytesAsync(mapping, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalGameMappings.SaveConfirmationAsync(mapping, mapping,
            legacy with { GamePackId = "gd:story:wrong" }, TestContext.Current.CancellationToken, expectedGameId: "local:legacy"));
        Assert.Equal(confirmed, await File.ReadAllBytesAsync(mapping, TestContext.Current.CancellationToken));
    }

    private GamePackViewModel NewPacks(LocalCommentSession session, TextSourceViewModel text, StoryLocatorViewModel story) =>
        new(session, text, story, roots: [Path.Combine(_root, "packs")],
            mappingPath: Path.Combine(_root, "game-mappings.local.json"), identitySettingsDirectory: _root);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReassociationRepairsMetadataConflictOrAddsMovedLegacyInstallation(bool metadataConflict)
    {
        MakePack("gd:story:replacement");
        string game = Path.Combine(_root, "game.exe");
        string priorPath = metadataConflict ? game : Path.Combine(_root, "old", "game.exe");
        var prior = new LocalGameMapping("old", "Prior game", "game.exe", priorPath, "gd:story:prior",
            metadataConflict ? "lunatranslator" : null, metadataConflict ? "one" : null,
            metadataConflict ? "vndb:v1" : null, metadataConflict ? "v1" : null);
        string mapping = Path.Combine(_root, "game-mappings.local.json");
        await File.WriteAllTextAsync(mapping, JsonSerializer.Serialize(new { version = 1, games = new[] { prior } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), TestContext.Current.CancellationToken);
        using var session = new LocalCommentSession(new EmptyRepository());
        await using var text = NewTextSource();
        await using var story = new StoryLocatorViewModel(text, "unused.json");
        await using var packs = NewPacks(session, text, story);
        if (metadataConflict) await packs.ConfigureLunaLibraryAsync(await WriteLibraryAsync(game));
        await packs.RefreshAsync();
        await packs.BindWindowAsync(game);
        Assert.Null(packs.ActivePack);
        Assert.False(packs.ConfirmAssociationCommand.CanExecute(null));
        Assert.True(packs.BeginReassociationCommand.CanExecute(null));
        packs.BeginReassociationCommand.Execute(null);
        await packs.ConfirmAssociationCommand.ExecuteAsync(null);
        Assert.Equal("gd:story:replacement", packs.ActivePack?.GameId);
        LocalGameMappings saved = await LocalGameMappings.LoadAsync(mapping, TestContext.Current.CancellationToken);
        Assert.Equal("gd:story:replacement", saved.MatchExact(game).Game?.RequiredGamePackId);
        if (!metadataConflict) Assert.Equal(prior, saved.MatchExact(priorPath).Game);
    }

    private static TextSourceViewModel NewTextSource() => new(new TextSourceOptions(),
        (action, _) => { action(); return Task.CompletedTask; }, (_, _) => new WaitingSource());

    private string MakePack(string gameId)
    {
        string directory = Path.Combine(_root, "packs", "pack");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "gamepack.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, GameId = gameId, Name = "Fixture", Version = "1", StoryIndex = "story.json",
            TextSource = new { Kind = "Luna" }
        }));
        File.WriteAllText(Path.Combine(directory, "story.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, GameId = gameId, Nodes = new[] { new { Id = "n1", Text = "Synthetic verification text", NextIds = Array.Empty<string>() } }
        }));
        return directory;
    }

    private async Task<string> WriteLibraryAsync(string game, bool duplicate = false)
    {
        string file = Path.Combine(_root, "savegamedata_5.3.1.json");
        var records = new Dictionary<string, object> { ["one"] = new { gamepath = game, title = "Fixture Game", vid = 60663 } };
        if (duplicate) records["two"] = new { gamepath = game, title = "Other version" };
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new object[] { records.Keys.ToArray(), records }));
        return file;
    }

    private sealed class WaitingSource : ITextSource
    {
        public Task RunAsync(Func<TextSourceEvent, CancellationToken, ValueTask> onEvent, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class EmptyRepository : ICommentRepository
    {
        public Task<IReadOnlyList<StoredDanmaku>> LoadAsync(string gameId, string nodeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredDanmaku>>([]);
        public Task SaveAsync(StoredDanmaku comment, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
