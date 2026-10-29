using System.Text;
using GalDanmaku.Core.GamePacks;
using GalDanmaku.Core.Story;
using GalDanmaku.Core.Text;

namespace GalDanmaku.Tests;

public sealed class GamePackTests : IDisposable
{
    private const string StoryJson = """{"SchemaVersion":1,"GameId":"pack-test","Nodes":[{"Id":"n1","Text":"こんにちは。","NextIds":[]}]}""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "galdanmaku-gamepack-" + Guid.NewGuid().ToString("N"));

    public GamePackTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Manifest(string storyIndex = "story.json", string gameId = "pack-test", string extra = "",
        string textSource = """{"Kind":"Mock"}""")
        => $$"""
            {"SchemaVersion":1,"GameId":"{{gameId}}","Name":"Test","Version":"1","StoryIndex":{{System.Text.Json.JsonSerializer.Serialize(storyIndex)}},
             "TextSource":{{textSource}}{{extra}}}
            """;

    private string MakePack(string manifest, string story = StoryJson, string name = "pack")
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "gamepack.json"), manifest, new UTF8Encoding(false));
        if (story is not null) File.WriteAllText(Path.Combine(directory, "story.json"), story, new UTF8Encoding(false));
        return directory;
    }

    private static Task<LoadedGamePack> Load(string directory) => GamePackLoader.LoadAsync(directory, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ValidPackLoadsAndExposesDeclarativeData()
    {
        string directory = MakePack(Manifest(extra: """
            ,"Engine":"X","Identification":{"ProcessNames":["Game.EXE","other"]},"Normalization":{"RemoveStrings":["[br]","<ruby>"]}
            """, textSource: """{"Kind":"Mock","MergeGapMilliseconds":10,"MockScript":["","あ"]}"""));
        LoadedGamePack loaded = await Load(directory);

        Assert.Equal("pack-test", loaded.Pack.GameId);
        Assert.Equal("X", loaded.Pack.Engine);
        Assert.Equal(Path.GetFullPath(directory), loaded.Pack.Directory);
        Assert.Equal(10, loaded.Pack.TextSource.Options.MergeGapMilliseconds);
        Assert.Equal(["", "あ"], loaded.Pack.TextSource.MockScript);
        Assert.True(loaded.Pack.MatchesProcess("game"));
        Assert.True(loaded.Pack.MatchesProcess("GAME.exe"));
        Assert.True(loaded.Pack.MatchesProcess("Other.exe"));
        Assert.False(loaded.Pack.MatchesProcess("ame.exe"));
        Assert.False(loaded.Pack.MatchesProcess(""));
        Assert.Equal("今日は", loaded.Pack.CleanText("今[br]日<ruby>は"));
        Assert.Equal("n1", loaded.Index.Nodes.Keys.Single());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/etc/story.json")]
    [InlineData("\\story.json")]
    [InlineData("C:\\story.json")]
    [InlineData("C:story.json")]
    [InlineData("\\\\server\\share\\story.json")]
    [InlineData("../story.json")]
    [InlineData("sub/../../story.json")]
    [InlineData("sub\\..\\..\\story.json")]
    [InlineData("sub/..\\..\\story.json")]
    [InlineData("./story.json")]
    [InlineData("a//story.json")]
    [InlineData("story.json:stream")]
    [InlineData("sto*ry.json")]
    [InlineData("CON.json")]
    [InlineData("story.json.")]
    public async Task EscapingOrMalformedStoryPathsAreRejected(string path)
    {
        string directory = MakePack(Manifest(storyIndex: path));
        File.WriteAllText(Path.Combine(_root, "story.json"), StoryJson);
        await Assert.ThrowsAsync<InvalidDataException>(() => Load(directory));
    }

    [Fact]
    public async Task SymbolicLinkLeavingThePackIsRejected()
    {
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "story.json"), StoryJson);
        string directory = MakePack(Manifest(storyIndex: "link/story.json"));
        try { Directory.CreateSymbolicLink(Path.Combine(directory, "link"), outside); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("当前环境无法创建符号链接。");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Load(directory));
    }

    [Fact]
    public async Task GameIdMismatchBetweenManifestAndIndexIsRejected()
    {
        string directory = MakePack(Manifest(gameId: "other-game"));
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() => Load(directory));
        Assert.Contains("GameId", exception.Message);
    }

    private const int GamePackManifestLimit = 64 * 1024;

    [Fact]
    public async Task CatalogReportsBrokenPacksWithoutStoppingAndMissingRootIsEmpty()
    {
        Assert.Empty(await GamePackCatalog.DiscoverAsync(Path.Combine(_root, "nope"), TestContext.Current.CancellationToken));
        MakePack(Manifest(), name: "good");
        MakePack("{ not json", name: "bad");
        Directory.CreateDirectory(Path.Combine(_root, "no-manifest"));

        var entries = await GamePackCatalog.DiscoverAsync(_root, TestContext.Current.CancellationToken);
        Assert.Equal(2, entries.Count);
        Assert.Null(entries.Single(entry => entry.DisplayName == "Test").Error);
        Assert.NotNull(entries.Single(entry => entry.DisplayName == "bad").Error);
    }

}
