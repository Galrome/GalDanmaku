using System.Text;
using System.Text.Json;
using GalDanmaku.Core.Story;
using GalDanmaku.Core.Text;

namespace GalDanmaku.Tests;

public sealed class StoryIndexTests
{
    [Fact]
    public void StableIdsAndInvertedCandidatesSurviveNodeOrderAndSerialization()
    {
        StoryNode[] nodes = [new("route:a", "  同じ\r\n言葉。 ", ["route:end"]),
            new("route:b", "同じ 言葉。", ["route:end"]), new("route:end", "また明日。", [])];
        var first = new StoryIndex(1, "synthetic", nodes);
        var second = StoryIndex.Parse(JsonSerializer.Serialize(new { SchemaVersion = 1, GameId = "synthetic", Nodes = nodes.Reverse() }));

        Assert.Equal(["route:a", "route:b"], first.FindExact("同じ 言葉。").Select(node => node.Id));
        Assert.Equal(first.FindExact("同じ 言葉。").Select(node => node.Id), second.FindExact("同じ 言葉。").Select(node => node.Id));
        Assert.Equal("また明日。", second.Nodes["route:end"].Text);
    }

    [Fact]
    public void PrefixLookupContainsEveryLongerCandidateWithoutChangingExplicitIds()
    {
        var index = new StoryIndex(1, "synthetic", [new("short", "風", []), new("long", "風が吹く。", []), new("other", "雪。", [])]);
        Assert.Equal(["long", "short"], index.FindPrefix("風").Select(node => node.Id));
        Assert.Equal("short", index.FindExact("風").Single().Id);
    }

    [Theory]
    [InlineData("\uFEFF  今日は\r\n  晴れ。\t ")]
    [InlineData("\u200B中文\u0000，！？\u2060")]
    [InlineData("cafe\u0301　ガ")]
    [InlineData("家族👩‍👩‍👧‍👦 ❤️！")]
    public void IndexUsesExactlyTheDialogueProcessorsNormalization(string raw)
    {
        var processor = new DialogueProcessor(TimeSpan.Zero);
        string expected = processor.Process(raw, DateTimeOffset.UtcNow).Event!.Text;
        var index = new StoryIndex(1, "synthetic", [new("node", raw, [])]);
        Assert.Equal(expected, index.Nodes["node"].NormalizedText);
        Assert.Equal("node", index.FindExact(expected).Single().Id);
    }

    public static TheoryData<string> InvalidJson => new()
    {
        "null", "[]", "{}", "{\"SchemaVersion\":2,\"GameId\":\"test\",\"Nodes\":[]}",
        "{\"SchemaVersion\":1,\"GameId\":null,\"Nodes\":[]}",
        "{\"SchemaVersion\":1,\"GameId\":\" test \",\"Nodes\":[]}",
        "{\"SchemaVersion\":1,\"GameId\":\"test\",\"Nodes\":null}",
        "{\"SchemaVersion\":1,\"GameId\":\"test\",\"Nodes\":[]}",
        JsonFor("[null]"),
        JsonFor("[{\"Id\":\"a\",\"Text\":null,\"NextIds\":[]}]"),
        JsonFor("[{\"Id\":\"a\",\"Text\":\" \",\"NextIds\":[]}]"),
        JsonFor("[{\"Id\":null,\"Text\":\"台詞\",\"NextIds\":[]}]"),
        JsonFor("[{\"Id\":\"bad id\",\"Text\":\"台詞\",\"NextIds\":[]}]"),
        JsonFor("[{\"Id\":\"a\",\"Text\":\"台詞\",\"NextIds\":null}]"),
        JsonFor("[{\"Id\":\"a\",\"Text\":\"台詞\",\"NextIds\":[null]}]"),
        JsonFor("[{\"Id\":\"a\",\"Text\":\"台詞\",\"NextIds\":[\"missing\"]}]"),
        JsonFor("[{\"Id\":\"a\",\"Text\":\"台詞\",\"NextIds\":[\"a\",\"a\"]}]"),
        JsonFor("[{\"Id\":\"a\",\"Text\":\"台詞\",\"NextIds\":[]},{\"Id\":\"a\",\"Text\":\"別\",\"NextIds\":[]}]")
    };

    private static string JsonFor(string nodes) => "{\"SchemaVersion\":1,\"GameId\":\"test\",\"Nodes\":" + nodes + "}";
}
