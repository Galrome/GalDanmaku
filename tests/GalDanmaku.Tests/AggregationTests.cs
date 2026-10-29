using GalDanmaku.Core;

namespace GalDanmaku.Tests;

public sealed class AggregationTests
{
    [Fact]
    public void ProjectionDeduplicatesIdsAndToggleRestoresOriginalComments()
    {
        StoredDanmaku first = LocalPlaybackTests.Comment(1000, text: " a\u00a0\u3000b ", fontSize: 28, color: "#FFD166");
        StoredDanmaku second = LocalPlaybackTests.Comment(2000, text: "a b", fontSize: 48);
        var playback = new NodePlayback { AggregationEnabled = true };
        playback.Load(first.GameId, first.NodeId, [second, first, first]);
        playback.AddSaved(second);
        Assert.Equal(2, playback.SavedCount);
        DanmakuDisplay aggregate = Assert.Single(playback.Displays);
        Assert.Equal(2, aggregate.Count);
        Assert.EndsWith(" ×2", aggregate.Text);
        Assert.Equal(1000, aggregate.OffsetMilliseconds);
        Assert.Equal("#FFD166", aggregate.Color);
        Assert.Null(aggregate.Source);
        Assert.False(aggregate.CanLike);
        Assert.True(aggregate.FontSize > first.FontSize);

        playback.IsPlaying = true;
        playback.AdvanceDisplay(2, _ => true);
        playback.AggregationEnabled = false;
        Assert.Equal(0, playback.OffsetMilliseconds);
        var originals = new List<StoredDanmaku>();
        playback.Advance(2, item => { originals.Add(item); return true; });
        Assert.Equal([first, second], originals);
        Assert.All(playback.Displays, display => Assert.True(display.CanLike && display.Source is not null));

        playback.AggregationEnabled = true;
        playback.Load(first.GameId, first.NodeId, [first, second]);
        Assert.Equal(aggregate.DisplayId, Assert.Single(playback.Displays).DisplayId);
        Assert.Equal(2, Assert.Single(playback.Displays).Count);
        playback.Clear();
        Assert.Empty(playback.Displays);
    }

    [Fact]
    public void MeaningfulUnicodeCaseAndNodeDifferencesArePreserved()
    {
        var playback = new NodePlayback { AggregationEnabled = true };
        StoredDanmaku[] comments = [LocalPlaybackTests.Comment(text: "A"), LocalPlaybackTests.Comment(text: "a"),
            LocalPlaybackTests.Comment(text: "a\u200b"), LocalPlaybackTests.Comment(text: "Ａ")];
        playback.Load("mock-game", "node-a", comments);
        Assert.Equal(4, playback.Displays.Count);
        Assert.Throws<ArgumentException>(() => playback.AddSaved(LocalPlaybackTests.Comment(node: "other")));
        Assert.Throws<ArgumentException>(() => playback.AddSaved(LocalPlaybackTests.Comment(game: "other")));
    }

    [Fact]
    public void SendingReplacesPendingAndActiveGroupWithoutReplayingTheClock()
    {
        var playback = new NodePlayback { AggregationEnabled = true };
        StoredDanmaku first = LocalPlaybackTests.Comment();
        playback.Load(first.GameId, first.NodeId, [first]);
        playback.IsPlaying = true;
        playback.AdvanceDisplay(1, _ => false);
        Assert.Equal(1, playback.PendingCount);
        long resetVersion = playback.ResetVersion;
        playback.AddSaved(LocalPlaybackTests.Comment(1000));
        Assert.Equal(0, playback.PendingCount);
        var engine = new DanmakuEngine();
        engine.Configure(new DanmakuSettings(), 800, 600);
        playback.AdvanceDisplay(0, display => engine.TryAdd(display, 180));
        DanmakuItem previous = Assert.Single(engine.Items);
        Assert.Equal(2, previous.Display!.Count);

        playback.AddSaved(LocalPlaybackTests.Comment(1000));
        Assert.False(playback.IsCurrentDisplay(previous.Display));
        Assert.True(engine.RemoveDisplay(previous.Display.DisplayId));
        playback.AdvanceDisplay(0, display => engine.TryAdd(display, 200));
        DanmakuItem replacement = Assert.Single(engine.Items);
        Assert.Equal(previous.Display.DisplayId, replacement.Display!.DisplayId);
        Assert.Equal(3, replacement.Display.Count);
        Assert.Equal(1000, playback.OffsetMilliseconds);
        Assert.Equal(resetVersion, playback.ResetVersion);
        playback.AdvanceDisplay(0, _ => throw new InvalidOperationException("Group was scheduled twice."));
    }

    [Fact]
    public void GrowthDiminishesAndGeometryUsesFinalDisplayFont()
    {
        static DanmakuDisplay Display(int count, double savedFontSize = 28)
        {
            var playback = new NodePlayback { AggregationEnabled = true };
            playback.Load("mock-game", "node-a", Enumerable.Range(0, count).Select(_ => LocalPlaybackTests.Comment(fontSize: savedFontSize)));
            return Assert.Single(playback.Displays);
        }

        DanmakuDisplay two = Display(2);
        DanmakuDisplay three = Display(3);
        DanmakuDisplay four = Display(4);
        Assert.True(three.FontSize - two.FontSize > four.FontSize - three.FontSize);
        Assert.Equal(two.FontSize, Display(2, 12).FontSize);
        Assert.Equal(two.FontSize, Display(2, 64).FontSize);
        Assert.Equal(Display(32).FontSize, Display(200).FontSize);
        Assert.Equal(49, Display(200).FontSize);
        Assert.Equal(49, Display(200, 12).FontSize);
        Assert.Equal(49, Display(200, 64).FontSize);

        var engine = new DanmakuEngine();
        engine.Configure(new DanmakuSettings(areaFraction: 1), 800, 70);
        Assert.True(engine.CanFit(LocalPlaybackTests.Comment(fontSize: 28)));
        Assert.False(engine.CanFit(four));
        engine.Configure(new DanmakuSettings(areaFraction: 1), 800, 200);
        Assert.True(engine.TryAdd(four, 240));
        Assert.True(engine.TryAdd(two, 180));
        Assert.Equal(four.FontSize, engine.Items[0].FontSize);
        Assert.True(engine.Items[1].Y >= engine.Items[0].Y + four.FontSize * 1.6);
        Assert.Equal(240, engine.Items[0].Width);
        Assert.Equal(four.Text, engine.Items[0].Text);
    }
}
