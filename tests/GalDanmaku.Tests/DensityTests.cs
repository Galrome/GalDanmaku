using GalDanmaku.Core;

namespace GalDanmaku.Tests;

public sealed class DensityTests
{
    private static NodePlayback Load(int count, long offset = 0)
    {
        var playback = new NodePlayback();
        playback.Load("mock-game", "node-a", Enumerable.Range(0, count).Select(_ => LocalPlaybackTests.Comment(offset)));
        playback.IsPlaying = true;
        return playback;
    }

    [Fact]
    public void PerFrameEmitCallsAreBoundedByPendingLimit()
    {
        NodePlayback playback = Load(5000);
        for (int frame = 0; frame < 20; frame++)
        {
            int calls = 0;
            playback.Advance(1.0 / 60, _ => { calls++; return false; });
            Assert.True(calls <= NodePlayback.MaximumPendingComments);
            Assert.True(playback.PendingCount <= NodePlayback.MaximumPendingComments);
        }
    }

    [Fact]
    public void VeryLateBacklogIsDroppedAndReplayRestoresIt()
    {
        NodePlayback playback = Load(5000);
        playback.Advance(NodePlayback.MaximumLatenessMilliseconds / 1000 + 1, _ => false);
        Assert.True(playback.PendingCount <= NodePlayback.MaximumPendingComments);
        playback.Advance(NodePlayback.MaximumPendingMilliseconds / 1000 + 1, _ => false);
        playback.Advance(NodePlayback.MaximumPendingMilliseconds / 1000 + 1, _ => false);
        Assert.Equal(0, playback.PendingCount);
        Assert.Equal(5000, playback.ExpiredCount);
        playback.Replay();
        Assert.Equal(0, playback.ExpiredCount);
        int shown = 0;
        playback.Advance(0, _ => { shown++; return true; });
        Assert.Equal(NodePlayback.MaximumPendingComments, shown);
    }

    [Fact]
    public void EngineStaysBoundedUnderDenseEmission()
    {
        var engine = new DanmakuEngine();
        engine.Configure(new DanmakuSettings(areaFraction: 1), 800, 600);
        NodePlayback playback = Load(5000);
        playback.Advance(0, c => engine.TryAdd(c, 100));
        Assert.InRange(engine.Items.Count, 1, DanmakuEngine.MaximumComments);
        engine.Advance(DanmakuEngine.ClampFrameSeconds(3600));
        Assert.InRange(engine.Items.Count, 0, DanmakuEngine.MaximumComments);
    }
}
