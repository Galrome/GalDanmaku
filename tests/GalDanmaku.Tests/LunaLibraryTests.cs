using System.Text.Json;
using GalDanmaku.Core.GameIdentity;
using GalDanmaku.Core.GamePacks;
using GalDanmaku.Integrations.LunaTranslator;

namespace GalDanmaku.Tests;

public sealed class LunaLibraryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GalDanmaku-Luna-" + Guid.NewGuid().ToString("N"));

    public LunaLibraryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Reader_PreservesSamePathUidsAndSkipsOnlyBrokenRecords()
    {
        string file = Path.Combine(_directory, "savegamedata_5.3.1.json");
        string game = Path.Combine(_directory, "game.exe");
        var records = new Dictionary<string, object?>
        {
            ["one"] = new { gamepath = game, title = "Game", vid = 60663, bgmsid = 611546, launchpath = "launcher.exe", launch_method = "direct" },
            ["two"] = new { gamepath = game, title = "Other configuration", vid = "v60663" },
            ["three"] = new { gamepath = Path.Combine(_directory, "other.exe"), vid = new { invalid = true }, bgmsid = false },
            ["broken"] = Array.Empty<string>(),
            ["missing-path"] = new { title = "Incomplete" }
        };
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new object[] { records.Keys.ToArray(), records }), TestContext.Current.CancellationToken);
        var provider = new LunaGameLibraryProvider(_directory);
        IReadOnlyList<GameLibraryEntry> games = await provider.GetGamesAsync(TestContext.Current.CancellationToken);
        Assert.True(provider.HasValidSnapshot);
        Assert.Equal(3, games.Count);
        Assert.Equal(2, games.Count(entry => LocalGameMappings.PathsEqual(entry.ExecutablePath, game)));
        Assert.Equal(new[] { "one", "two", "three" }, games.Select(entry => entry.SourceGameId));
        Assert.Equal("vndb:v60663", games[0].WorkId);
        Assert.Equal("v60663", games[0].VndbId);
        Assert.Equal("611546", games[0].BangumiId);
        Assert.Equal(Path.Combine(_directory, "launcher.exe"), games[0].LaunchPath);
        Assert.Equal("direct", games[0].LaunchMethod);
        Assert.Null(games[2].VndbId);
        Assert.Null(games[2].BangumiId);
    }

    [Fact]
    public async Task Reader_UsesCurrentListAndGroupsWithoutResurrectingUnlistedDetails()
    {
        string file = Path.Combine(_directory, "current.json");
        var records = new Dictionary<string, object>
        {
            ["listed"] = new { gamepath = Path.Combine(_directory, "not-installed.exe") },
            ["group-only"] = new { gamepath = Path.Combine(_directory, "group.exe") },
            ["historical"] = new { gamepath = Path.Combine(_directory, "old.exe") }
        };
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new object?[]
        {
            new[] { "listed" }, records, new object?[] { null, 1, new { games = new[] { "listed", "group-only" } } }
        }), TestContext.Current.CancellationToken);
        var provider = new LunaGameLibraryProvider(file);
        IReadOnlyList<GameLibraryEntry> games = await provider.GetGamesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "listed", "group-only" }, games.Select(entry => entry.SourceGameId));
        Assert.Contains("1 条", provider.Status);
        Assert.Contains("未导入", provider.Status);
        // An explicitly empty library must not revive all retained details.
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new object[] { Array.Empty<string>(), records }),
            TestContext.Current.CancellationToken);
        Assert.Empty(await provider.GetGamesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LegacyMappingGetsUniqueMetadataWithoutChangingItsStoryIdentity()
    {
        string game = Path.Combine(_directory, "game.exe");
        string file = Path.Combine(_directory, "legacy.json");
        var legacy = new LocalGameMapping("local:legacy", "Legacy game", "game.exe", game);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { version = 1, games = new[] { legacy } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), TestContext.Current.CancellationToken);
        LocalGameMappings mappings = await LocalGameMappings.LoadAsync(file, TestContext.Current.CancellationToken);
        byte[] before = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
        var entry = new GameLibraryEntry("lunatranslator", "uid", game, "Luna name", "vndb:v60663", "v60663", "611546");
        GameIdentityResult result = GameIdentityResolver.Resolve(game, mappings, [entry]);
        Assert.Equal("local:legacy", result.GameId);
        Assert.Equal("v60663", result.VndbId);
        Assert.Equal("611546", result.BangumiId);
        Assert.Single(result.Candidates);
        Assert.Contains("Luna", result.Source);
        Assert.Equal(before, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        GameIdentityResult multiple = GameIdentityResolver.Resolve(game, mappings, [entry, entry with { SourceGameId = "other" }]);
        Assert.Equal("local:legacy", multiple.GameId);
        Assert.Null(multiple.VndbId);
        Assert.Equal(2, multiple.Candidates.Count);
    }

    [Fact]
    public async Task Provider_FallsBackToValidSnapshotAfterMalformedOrMissingFile()
    {
        string file = await WriteLibraryAsync();
        var provider = new LunaGameLibraryProvider(file);
        IReadOnlyList<GameLibraryEntry> original = await provider.GetGamesAsync(TestContext.Current.CancellationToken);
        Assert.Single(original);
        await File.WriteAllTextAsync(file, "{", TestContext.Current.CancellationToken);
        Assert.Same(original, await provider.GetGamesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("快照", provider.Status);
        File.Delete(file);
        Assert.Same(original, await provider.GetGamesAsync(TestContext.Current.CancellationToken));
        var missing = new LunaGameLibraryProvider(file);
        Assert.Empty(await missing.GetGamesAsync(TestContext.Current.CancellationToken));
        Assert.False(missing.HasValidSnapshot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetGamesAsync(cancellation.Token));
    }

    [Fact]
    public async Task Provider_RejectsExcessiveSizeDepthAndUnsupportedSchema()
    {
        string file = Path.Combine(_directory, "oversize.json");
        await using (var stream = File.Create(file)) stream.SetLength(16 * 1024 * 1024 + 1);
        var oversized = new LunaGameLibraryProvider(file);
        Assert.Empty(await oversized.GetGamesAsync(TestContext.Current.CancellationToken));
        Assert.False(oversized.HasValidSnapshot);
        Assert.Contains("16 MiB", oversized.Status);
        await File.WriteAllTextAsync(file, new string('[', 70) + "0" + new string(']', 70), TestContext.Current.CancellationToken);
        var deep = new LunaGameLibraryProvider(file);
        Assert.Empty(await deep.GetGamesAsync(TestContext.Current.CancellationToken));
        Assert.False(deep.HasValidSnapshot);
        await File.WriteAllTextAsync(file, "{}", TestContext.Current.CancellationToken);
        var unsupported = new LunaGameLibraryProvider(file);
        Assert.Empty(await unsupported.GetGamesAsync(TestContext.Current.CancellationToken));
        Assert.False(unsupported.HasValidSnapshot);
    }

    [Fact]
    public async Task Resolver_RequiresConfirmationAndKeepsSavedIdentityAcrossLunaChanges()
    {
        string game = Path.Combine(_directory, "game.exe");
        var entry = new GameLibraryEntry("lunatranslator", "uid", game, "Game", "vndb:v60663", "v60663");
        GameIdentityResult candidate = GameIdentityResolver.Resolve(game, LocalGameMappings.Empty, [entry]);
        Assert.Null(candidate.GameId);
        Assert.Equal("vndb:v60663", candidate.WorkId);
        GameIdentityResult ambiguous = GameIdentityResolver.Resolve(game, LocalGameMappings.Empty,
            [entry, entry with { SourceGameId = "other-uid" }]);
        Assert.Null(ambiguous.GameId);
        Assert.Equal(2, ambiguous.Candidates.Count);

        string mappingFile = Path.Combine(_directory, "mappings.json");
        var saved = new LocalGameMapping("local-identity", "Confirmed game", "game.exe", game,
            "gd:story:confirmed", entry.ProviderId, entry.SourceGameId, entry.WorkId, entry.VndbId);
        await LocalGameMappings.SaveConfirmationAsync(mappingFile, mappingFile, saved, TestContext.Current.CancellationToken);
        LocalGameMappings mappings = await LocalGameMappings.LoadAsync(mappingFile, TestContext.Current.CancellationToken);
        Assert.Equal(saved.RequiredGamePackId, GameIdentityResolver.Resolve(game, mappings, []).GameId);
        Assert.Equal(saved.RequiredGamePackId, GameIdentityResolver.Resolve(game, mappings,
            [entry with { SourceGameId = "changed", WorkId = "vndb:v1" }, entry]).GameId);

        string moved = Path.Combine(_directory, "moved", "game.exe");
        Assert.Null(GameIdentityResolver.Resolve(moved, mappings,
            [entry with { ExecutablePath = moved }]).GameId);
        Assert.Null(GameIdentityResolver.Resolve(game, mappings,
            [entry with { WorkId = "vndb:v1", VndbId = "v1" }]).GameId);
        Assert.Null(GameIdentityResolver.Resolve(moved, mappings,
            [entry with { ExecutablePath = moved, SourceGameId = "new-uid" }]).GameId);
        Assert.Null(GameIdentityResolver.Resolve(moved, mappings, []).GameId);
    }

    [Fact]
    public async Task Confirmation_RoundTripsAndNeverOverwritesConflictingOrInvalidMappings()
    {
        string file = Path.Combine(_directory, "mappings.json");
        var saved = new LocalGameMapping("confirmed", "Game", "game.exe", Path.Combine(_directory, "game.exe"),
            "gd:story:one", "lunatranslator", "uid", "vndb:v60663", "v60663", "611546", ExactPathOnly: true);
        await LocalGameMappings.SaveConfirmationAsync(file, file, saved, TestContext.Current.CancellationToken);
        LocalGameMappings loaded = await LocalGameMappings.LoadAsync(file, TestContext.Current.CancellationToken);
        Assert.Equal(saved, loaded.MatchExact(saved.ExecutablePath).Game);
        Assert.Equal(saved, loaded.MatchSource("lunatranslator", "uid").Game);
        byte[] original = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalGameMappings.SaveConfirmationAsync(file, file,
            saved with { Id = "conflict", GamePackId = "gd:story:two" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalGameMappings.SaveConfirmationAsync(file, file,
            saved with { Id = "source-conflict", GamePackId = "gd:story:two", ExecutablePath = Path.Combine(_directory, "moved", "game.exe") }, TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(file, "invalid existing configuration", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<JsonException>(() => LocalGameMappings.SaveConfirmationAsync(file, file, saved, TestContext.Current.CancellationToken));
        Assert.Equal("invalid existing configuration", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        string destination = Path.Combine(_directory, "destination.json");
        await File.WriteAllTextAsync(destination, "also invalid", TestContext.Current.CancellationToken);
        File.Delete(file);
        await Assert.ThrowsAsync<JsonException>(() => LocalGameMappings.SaveConfirmationAsync(file, destination, saved, TestContext.Current.CancellationToken));
        Assert.Equal("also invalid", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { version = 1, games = new[] { saved with { GamePackId = null } } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalGameMappings.LoadAsync(file, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RealLibrary_ReadOnlyValidationWhenExplicitlyConfigured()
    {
        string? file = Environment.GetEnvironmentVariable("GALDANMAKU_LUNA_VALIDATION_FILE");
        if (string.IsNullOrWhiteSpace(file)) return;
        var provider = new LunaGameLibraryProvider(file);
        IReadOnlyList<GameLibraryEntry> games = await provider.GetGamesAsync(TestContext.Current.CancellationToken);
        Assert.True(provider.HasValidSnapshot, provider.Status);
        TestContext.Current.TestOutputHelper?.WriteLine(provider.Status);
        GameLibraryEntry game = Assert.Single(games, entry => string.Equals(Path.GetFileName(entry.ExecutablePath),
            "totsulover.exe", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(game.SourceGameId));
        Assert.Equal("v60663", game.VndbId);
        Assert.Equal("611546", game.BangumiId);
        GameIdentityResult result = GameIdentityResolver.Resolve(game.ExecutablePath, LocalGameMappings.Empty, games);
        Assert.Contains(result.Candidates, candidate => candidate.SourceGameId == game.SourceGameId);
        Assert.Null(result.GameId);
        string? mappingFile = Environment.GetEnvironmentVariable("GALDANMAKU_MAPPING_VALIDATION_FILE");
        if (!string.IsNullOrWhiteSpace(mappingFile))
        {
            LocalGameMappings mappings = await LocalGameMappings.LoadAsync(mappingFile, TestContext.Current.CancellationToken);
            LocalGameMapping saved = Assert.IsType<LocalGameMapping>(mappings.MatchExact(game.ExecutablePath).Game);
            GameIdentityResult enriched = GameIdentityResolver.Resolve(game.ExecutablePath, mappings, games);
            Assert.Equal(saved.RequiredGamePackId, enriched.GameId);
            Assert.Equal("v60663", enriched.VndbId);
            Assert.Equal("611546", enriched.BangumiId);
            TestContext.Current.TestOutputHelper?.WriteLine($"Existing GameId retained; VNDB={enriched.VndbId}; Bangumi={enriched.BangumiId}.");
        }
    }

    private async Task<string> WriteLibraryAsync()
    {
        string file = Path.Combine(_directory, "savegamedata_5.3.1.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new object[]
        {
            new[] { "uid" }, new Dictionary<string, object> { ["uid"] = new { gamepath = Path.Combine(_directory, "game.exe"), title = "Game" } }
        }), TestContext.Current.CancellationToken);
        return file;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
