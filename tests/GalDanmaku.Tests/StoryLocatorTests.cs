using GalDanmaku.Core.Story;
using GalDanmaku.Core.Text;

namespace GalDanmaku.Tests;

public sealed class StoryLocatorTests
{
    private readonly Guid _session = Guid.NewGuid();
    private long _sequence;

    [Theory]
    [InlineData("a", "青い花。", "花を飾ろう。")]
    [InlineData("b", "赤い傘。", "傘を置こう。")]
    public void SequentialReadingDistinguishesRoutesAndConverges(string route, string intro, string ending)
    {
        var locator = new StoryLocator(Routes());
        AssertConfirmed(locator.Process(New(1, intro)), route + "0");
        AssertConfirmed(locator.Process(New(2, "また会えたね。")), route + "1");
        AssertConfirmed(locator.Process(New(3, "少し歩こう。")), route + "2");
        AssertConfirmed(locator.Process(New(4, ending)), route + "3");
        AssertConfirmed(locator.Process(New(5, "明日は晴れる。")), "end");
    }

    [Fact]
    public void MissingRouteHistoryRemainsAmbiguousUntilRoutesConverge()
    {
        var locator = new StoryLocator(Routes());
        AssertAmbiguous(locator.Process(New(1, "また会えたね。")), "a1", "b1");
        Assert.Null(locator.OffsetMilliseconds);
        AssertAmbiguous(locator.Process(New(2, "少し歩こう。")), "a2", "b2");
        AssertConfirmed(locator.Process(New(3, "明日は晴れる。")), "end");
    }

    [Fact]
    public void OneMissedNodeCanRecoverUsingRouteContext()
    {
        var locator = new StoryLocator(Routes());
        locator.Process(New(1, "青い花。"));
        AssertConfirmed(locator.Process(New(2, "少し歩こう。")), "a2");
    }

    [Fact]
    public void UnknownTextClearsRouteContextAndUniqueTextRecovers()
    {
        var locator = new StoryLocator(Routes());
        locator.Process(New(1, "青い花。"));
        StoryLocation unknown = locator.Process(New(2, "誤認識された文字列。"));
        Assert.Equal(StoryLocationStatus.Unknown, unknown.Status);
        Assert.Null(unknown.Node);
        Assert.Empty(unknown.Candidates);
        Assert.Null(locator.OffsetMilliseconds);
        AssertAmbiguous(locator.Process(New(3, "また会えたね。")), "a1", "b1");
        AssertConfirmed(locator.Process(New(4, "傘を置こう。")), "b3");
    }

    [Fact]
    public void ConsecutiveIdenticalTextWithNewDialogueIdAdvances()
    {
        var locator = new StoryLocator(Index(new("first", "はい。", ["second"]), new("second", "はい。", [])));
        AssertAmbiguous(locator.Process(New(1, "はい。")), "first", "second");
        AssertConfirmed(locator.Process(New(2, "はい。")), "second");
        Assert.Equal(2, locator.HistoryCount);
    }

    [Fact]
    public void TypingReplacesCurrentObservationAndKeepsPreviousRouteContext()
    {
        var locator = new StoryLocator(Routes());
        locator.Process(New(1, "青い花。"));
        StoryLocation partial = locator.Process(New(2, "また"));
        Assert.NotEqual(StoryLocationStatus.Confirmed, partial.Status);
        Assert.Null(partial.Node);
        Assert.Null(locator.OffsetMilliseconds);
        AssertConfirmed(locator.Process(Update(2, "また会えたね。")), "a1");
        Assert.Equal(2, locator.HistoryCount);
        AssertConfirmed(locator.Process(New(3, "少し歩こう。")), "a2");
    }

    [Fact]
    public void ExactShortTextDoesNotConfirmWhileLongerCompletionIsPossible()
    {
        var locator = new StoryLocator(Index(new("short", "風", []), new("long", "風が吹く。", [])));
        AssertAmbiguous(locator.Process(New(1, "風")), "long", "short");
        Assert.Null(locator.OffsetMilliseconds);
        AssertConfirmed(locator.Process(Update(1, "風が吹く。")), "long");
        Assert.Equal(1, locator.HistoryCount);
    }

    [Fact]
    public void StaleSequenceAndOldDialogueEventsCannotMoveLocationOrClock()
    {
        var clock = new ManualTimeProvider();
        var locator = new StoryLocator(Routes(), clock);
        DialogueEvent first = New(1, "青い花。");
        locator.Process(first);
        locator.Process(New(2, "また会えたね。"));
        clock.Advance(TimeSpan.FromMilliseconds(750));
        locator.Process(first);
        locator.Process(Update(1, "赤い傘。"));
        AssertConfirmed(locator.Current, "a1");
        Assert.Equal(750, locator.OffsetMilliseconds);
        Assert.Equal(2, locator.HistoryCount);
    }

    [Fact]
    public void SessionChangeResetsHistoryAndPermitsSequenceRestart()
    {
        var locator = new StoryLocator(Routes());
        locator.Process(New(10, "青い花。"));
        var changed = new DialogueEvent(Guid.NewGuid(), 1, 1, DateTimeOffset.UnixEpoch, DialogueEventKind.NewDialogue, "また会えたね。");
        AssertAmbiguous(locator.Process(changed), "a1", "b1");
        Assert.Equal(1, locator.HistoryCount);
    }

    [Fact]
    public void MissingNewDialogueEventCanRecoverFromFirstObservedUpdate()
    {
        var locator = new StoryLocator(Routes());
        locator.Process(New(1, "青い花。"));
        AssertConfirmed(locator.Process(Update(2, "少し歩こう。")), "a2");
        Assert.Equal(2, locator.HistoryCount);
    }

    private DialogueEvent New(long id, string text) => new(_session, id, ++_sequence, DateTimeOffset.UnixEpoch,
        DialogueEventKind.NewDialogue, text);

    private DialogueEvent Update(long id, string text) => new(_session, id, ++_sequence, DateTimeOffset.UnixEpoch,
        DialogueEventKind.TextUpdated, text);

    private static StoryIndex Index(params StoryNode[] nodes) => new(1, "synthetic", nodes);

    private static StoryIndex Routes() => Index(
        new("a0", "青い花。", ["a1"]), new("a1", "また会えたね。", ["a2"]),
        new("a2", "少し歩こう。", ["a3"]), new("a3", "花を飾ろう。", ["end"]),
        new("b0", "赤い傘。", ["b1"]), new("b1", "また会えたね。", ["b2"]),
        new("b2", "少し歩こう。", ["b3"]), new("b3", "傘を置こう。", ["end"]),
        new("end", "明日は晴れる。", []));

    private static void AssertConfirmed(StoryLocation result, string id)
    {
        Assert.Equal(StoryLocationStatus.Confirmed, result.Status);
        Assert.Equal(id, result.Node?.Id);
        Assert.Equal(id, Assert.Single(result.Candidates).Id);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    private static void AssertAmbiguous(StoryLocation result, params string[] ids)
    {
        Assert.Equal(StoryLocationStatus.Ambiguous, result.Status);
        Assert.Null(result.Node);
        Assert.Equal(ids, result.Candidates.Select(node => node.Id));
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
