using GalDanmaku.Core;

namespace GalDanmaku.Tests;

public sealed class LocalPlaybackTests
{
    internal static StoredDanmaku Comment(long offset = 0, string node = "node-a", string game = "mock-game",
        string text = "你好 ' world", double fontSize = 28, string color = "#FFFFFF", Guid? id = null) =>
        new(id ?? Guid.NewGuid(), game, node, text, DanmakuType.Scrolling, color, fontSize, offset,
            new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void PlaybackEmitsAtOffsetsInDeterministicOrderOnce()
    {
        StoredDanmaku later = Comment(1000);
        StoredDanmaku second = Comment(0, id: Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        StoredDanmaku first = Comment(0, id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var playback = new NodePlayback();
        playback.Load("mock-game", "node-a", [later, second, first]);
        playback.IsPlaying = true;
        var shown = new List<StoredDanmaku>();
        playback.Advance(0, item => { shown.Add(item); return true; });
        Assert.Equal([first, second], shown);
        playback.Advance(0.999, item => { shown.Add(item); return true; });
        Assert.Equal(2, shown.Count);
        playback.Advance(0.001, item => { shown.Add(item); return true; });
        playback.Advance(10, item => { shown.Add(item); return true; });
        Assert.Equal([first, second, later], shown);
    }

    [Fact]
    public void PausingDoesNotAdvanceTimeOrEmitNewlySavedComments()
    {
        var playback = new NodePlayback();
        playback.Load("mock-game", "node-a", []);
        playback.IsPlaying = true;
        playback.Advance(1, _ => true);
        playback.IsPlaying = false;
        StoredDanmaku sent = Comment(1000, fontSize: 48, color: "#FFD166");
        playback.AddSaved(sent);
        playback.Advance(60, _ => throw new InvalidOperationException("Paused playback must not emit."));
        Assert.Equal(1000, playback.OffsetMilliseconds);
        playback.IsPlaying = true;
        StoredDanmaku? shown = null;
        playback.Advance(0, item => { shown = item; return true; });
        Assert.Equal(sent, shown);
        playback.Advance(0, _ => throw new InvalidOperationException("Duplicate emission."));
    }

    [Fact]
    public void FullLanesRetainBoundedPendingQueueAndRetryWithoutDuplicates()
    {
        var playback = new NodePlayback();
        StoredDanmaku[] comments = Enumerable.Range(0, 300).Select(_ => Comment()).ToArray();
        playback.Load("mock-game", "node-a", comments);
        playback.IsPlaying = true;
        playback.Advance(0, _ => false);
        Assert.Equal(NodePlayback.MaximumPendingComments, playback.PendingCount);
        var shown = new HashSet<Guid>();
        for (int i = 0; i < 10; i++)
        {
            playback.Advance(1, item => { Assert.True(shown.Add(item.Id)); return true; });
            Assert.InRange(playback.PendingCount, 0, NodePlayback.MaximumPendingComments);
        }

        Assert.Equal(300, shown.Count);
    }

    [Fact]
    public void ReplayAndSwitchResetTimeAndDiscardOldPendingEntries()
    {
        var playback = new NodePlayback();
        StoredDanmaku a = Comment();
        playback.Load("mock-game", "node-a", [a]);
        playback.IsPlaying = true;
        playback.Advance(2, _ => false);
        playback.Replay();
        Assert.Equal(0, playback.OffsetMilliseconds);
        var shown = new List<StoredDanmaku>();
        playback.Advance(0, item => { shown.Add(item); return true; });
        Assert.Equal(a, Assert.Single(shown));
        playback.Replay();
        playback.Advance(0, _ => false);
        playback.Load("mock-game", "node-b", []);
        playback.Advance(20, _ => throw new InvalidOperationException("Old node leaked."));
        Assert.Equal(0, playback.PendingCount);
        Assert.Throws<ArgumentException>(() => playback.AddSaved(a));
    }

}
