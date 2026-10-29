using GalDanmaku.Core;

namespace GalDanmaku.Tests;

public sealed class DanmakuEngineTests
{
    [Fact]
    public void NewCommentStartsAtRightEdgeAndMovesByElapsedTime()
    {
        var engine = CreateEngine();
        Assert.True(engine.TryAdd("Hello", 100));
        Assert.Equal(800, Assert.Single(engine.Items).X);
        engine.Advance(0.5);
        Assert.Equal(730, Assert.Single(engine.Items).X);
    }

    [Fact]
    public void SplittingFrameTimeDoesNotChangeDistance()
    {
        var engine = CreateEngine();
        var other = CreateEngine();
        engine.TryAdd("Hello", 100);
        other.TryAdd("Hello", 100);
        engine.Advance(1);
        for (int i = 0; i < 10; i++)
        {
            other.Advance(0.1);
        }

        Assert.Equal(engine.Items[0].X, other.Items[0].X, 8);
    }

    [Fact]
    public void SameLaneWaitsUntilPreviousCommentHasEnoughSpace()
    {
        var engine = CreateEngine(new DanmakuSettings(areaFraction: 1), height: 64);
        Assert.True(engine.TryAdd("First", 100));
        Assert.False(engine.TryAdd("Second", 100));
        engine.Advance(1);
        Assert.True(engine.TryAdd("Second", 100));
    }

    [Fact]
    public void DisplayAreaLimitsLanesAndDoesNotOverflow()
    {
        var settings = new DanmakuSettings(areaFraction: 0.25);
        var engine = CreateEngine(settings, height: 400);
        while (engine.TryAdd("Line", 120))
        {
        }

        Assert.NotEmpty(engine.Items);
        Assert.All(engine.Items, item => Assert.True(item.Y + engine.LineHeight <= 100));
        Assert.Single(engine.Items);
    }

    [Fact]
    public void CapacityBoundsMemoryEvenWithVeryLargeViewport()
    {
        var engine = CreateEngine(height: 100_000);
        for (int i = 0; i < 1000; i++)
        {
            engine.TryAdd("Hello", 100);
        }

        Assert.Equal(DanmakuEngine.MaximumComments, engine.Items.Count);
    }

    private static DanmakuEngine CreateEngine(DanmakuSettings? settings = null, double height = 600)
    {
        var engine = new DanmakuEngine();
        engine.Configure(settings ?? new DanmakuSettings(), 800, height);
        return engine;
    }
}
