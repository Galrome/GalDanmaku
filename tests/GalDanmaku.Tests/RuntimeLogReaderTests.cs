using System.Text.Json;
using GalDanmaku.Core.Diagnostics;

namespace GalDanmaku.Tests;

public sealed class RuntimeLogReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GalDanmaku-log-reader-" + Guid.NewGuid().ToString("N"));
    public RuntimeLogReaderTests() => Directory.CreateDirectory(_root);
    private static string Line(int sequence) => JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow.AddSeconds(sequence),
        level = "Error", module = "Storage", @event = "SaveFailed", data = new { reason = "Busy", text = "private dialogue", path = "private path" }, exception = "private exception" });

    [Fact]
    public void SharedWriterMalformedAndIncompleteLinesRecoverOnNextSnapshot()
    {
        string path = Path.Combine(_root, "galdanmaku-2026-10-29.jsonl");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var text = new StreamWriter(writer) { AutoFlush = true };
        text.Write(Line(1) + "\ninvalid\n" + Line(2));
        var first = RuntimeLogReader.ReadRecent(_root, TestContext.Current.CancellationToken);
        Assert.Single(first.Records);
        Assert.Equal(1, first.Skipped);
        text.Write("\n");
        Assert.Equal(2, RuntimeLogReader.ReadRecent(_root, TestContext.Current.CancellationToken).Records.Count);
    }

    [Fact]
    public void DailyVolumesAndTruncationKeepRecordsBoundedAndFresh()
    {
        string path = Path.Combine(_root, "galdanmaku-2026-10-29.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, 700).Select(Line));
        File.WriteAllText(Path.Combine(_root, "galdanmaku-2026-10-29-001.jsonl"), Line(701) + "\n");
        var recent = RuntimeLogReader.ReadRecent(_root, TestContext.Current.CancellationToken);
        Assert.Equal(500, recent.Records.Count);
        Assert.True(recent.Records[0].Timestamp > recent.Records[^1].Timestamp);
        File.WriteAllText(path, Line(800) + "\n");
        Assert.Equal(2, RuntimeLogReader.ReadRecent(_root, TestContext.Current.CancellationToken).Records.Count);
    }

    [Fact]
    public void SafeExportRemovesAllDetailedFieldsButFullExportIsUnchanged()
    {
        File.WriteAllText(Path.Combine(_root, "galdanmaku-2026-10-29.jsonl"), Line(1) + "\n");
        var record = Assert.Single(RuntimeLogReader.ReadRecent(_root, TestContext.Current.CancellationToken).Records);
        string safe = record.Export(true);
        Assert.DoesNotContain("private", safe);
        Assert.Contains("SaveFailed", safe);
        Assert.Equal(record.Json, record.Export(false));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
