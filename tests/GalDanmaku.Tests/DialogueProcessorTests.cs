using GalDanmaku.Core.Text;

namespace GalDanmaku.Tests;

public sealed class DialogueProcessorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-15T00:00:00Z");
    private readonly DialogueProcessor _processor = new(TimeSpan.FromMilliseconds(1500));

    [Theory]
    [InlineData("\uFEFF  今日は\r\n  晴れ。\t ", "今日は 晴れ。")]
    [InlineData("\u200B中文\u0000，！？\u2060", "中文，！？")]
    [InlineData("cafe\u0301　ガ", "café ガ")]
    [InlineData("家族👩‍👩‍👧‍👦 ❤️！", "家族👩‍👩‍👧‍👦 ❤️！")]
    [InlineData("{\"text\":\"普通台词\"}", "{\"text\":\"普通台词\"}")]
    public void NormalizesConservatively(string raw, string expected)
    {
        Assert.Equal(expected, _processor.Process(raw, Start).Event?.Text);
    }

    [Fact]
    public void PrefixGrowthKeepsDialogueIdAndUpdatesTimestamp()
    {
        DialogueEvent first = Assert.IsType<DialogueEvent>(_processor.Process("你", Start).Event);
        DialogueEvent second = Assert.IsType<DialogueEvent>(_processor.Process("你好", Start.AddSeconds(1)).Event);
        DialogueEvent third = Assert.IsType<DialogueEvent>(_processor.Process("你好。", Start.AddSeconds(2)).Event);
        Assert.Equal(DialogueEventKind.NewDialogue, first.Kind);
        Assert.Equal(DialogueEventKind.TextUpdated, second.Kind);
        Assert.Equal(DialogueEventKind.TextUpdated, third.Kind);
        Assert.Equal(first.DialogueId, third.DialogueId);
        Assert.Equal(Start.AddSeconds(2), third.ReceivedAt);
        Assert.Equal(first.Sequence + 2, third.Sequence);
    }

    [Fact]
    public void ReconnectSuppressesRepeatedFrameButPreventsStalePrefixMerge()
    {
        _processor.Process("你", Start);
        _processor.OnDisconnected();
        Assert.Equal(TextFilterReason.Duplicate, _processor.Process("你", Start.AddMilliseconds(100)).FilterReason);
        Assert.Equal(DialogueEventKind.NewDialogue, _processor.Process("你好", Start.AddMilliseconds(200)).Event?.Kind);
        Assert.Equal(DialogueEventKind.TextUpdated, _processor.Process("你好。", Start.AddMilliseconds(300)).Event?.Kind);
    }

    [Fact]
    public void ResetCreatesNewSessionAndClearsDuplicateState()
    {
        DialogueEvent first = Assert.IsType<DialogueEvent>(_processor.Process("你好", Start).Event);
        _processor.Reset();
        DialogueEvent next = Assert.IsType<DialogueEvent>(_processor.Process("你好", Start).Event);
        Assert.NotEqual(first.SessionId, next.SessionId);
        Assert.Equal(1, next.DialogueId);
        Assert.Equal(1, next.Sequence);
    }

    [Fact]
    public void RejectsMalformedUtf16AndOversizedInputWithoutTruncating()
    {
        Assert.Equal(TextFilterReason.Invalid, _processor.Process("\uD800", Start).FilterReason);
        Assert.Equal(TextFilterReason.Invalid, _processor.Process(new string('a', 65537), Start).FilterReason);
        Assert.Equal("下一句", _processor.Process("下一句", Start).Event?.Text);
    }
}
