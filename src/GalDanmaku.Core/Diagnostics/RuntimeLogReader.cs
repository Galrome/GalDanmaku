using System.Text;
using System.Text.Json;

namespace GalDanmaku.Core.Diagnostics;

public sealed record RuntimeLogRecord(DateTimeOffset Timestamp, string Level, string Module, string Event,
    string Summary, string Json)
{
    public string LocalTime => Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss.fff");
    public string Export(bool redact)
    {
        if (!redact) return Json;
        // Allowlist rather than guessing every spelling of text, paths, credentials and exception payloads.
        // Detailed fields remain available locally; a safe export preserves only the event chronology.
        return JsonSerializer.Serialize(new { timestamp = Timestamp, level = Level, module = Module, @event = Event,
            detailsRedacted = true });
    }
}

public sealed record RuntimeLogSnapshot(IReadOnlyList<RuntimeLogRecord> Records, int Skipped, int UnavailableFiles);

public static class RuntimeLogReader
{
    public const int MaximumRecords = 500;
    private const int MaximumFiles = 8;
    private const int TailBytes = 512 * 1024;

    // A bounded snapshot intentionally rereads only recent file tails. No offsets survive rotation/truncation,
    // so renamed/rotated files and a previously incomplete final line recover on the next refresh.
    public static RuntimeLogSnapshot ReadRecent(string directory, CancellationToken token = default)
    {
        var records = new List<RuntimeLogRecord>();
        int skipped = 0, unavailable = 0;
        if (!Directory.Exists(directory)) return new([], 0, 0);
        FileInfo[] files = new DirectoryInfo(directory).EnumerateFiles("galdanmaku-*.jsonl")
            .OrderByDescending(file => file.LastWriteTimeUtc).Take(MaximumFiles).ToArray();
        foreach (FileInfo file in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                long start = Math.Max(0, stream.Length - TailBytes);
                stream.Position = start;
                byte[] buffer = new byte[(int)Math.Min(TailBytes, stream.Length - start)];
                int count = 0, read;
                while (count < buffer.Length && (read = stream.Read(buffer, count, buffer.Length - count)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    count += read;
                }
                int lineStart = 0;
                bool discardFirst = start > 0;
                for (int i = 0; i < count; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    token.ThrowIfCancellationRequested();
                    if (discardFirst) discardFirst = false;
                    else if (i > lineStart)
                    {
                        string json = Encoding.UTF8.GetString(buffer, lineStart, i - lineStart).Trim();
                        try
                        {
                            using var document = JsonDocument.Parse(json);
                            JsonElement root = document.RootElement;
                            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object.");
                            string Field(string name) => root.TryGetProperty(name, out JsonElement field)
                                && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
                            if (!DateTimeOffset.TryParse(Field("timestamp"), out var timestamp)) throw new JsonException("Invalid timestamp.");
                            string summary = Field("exception");
                            if (summary.Length == 0 && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                            {
                                foreach (string key in new[] { "reason", "Reason", "message", "result", "state", "status" })
                                    if (data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                                    { summary = value.GetString() ?? ""; break; }
                            }
                            summary = summary.Replace('\r', ' ').Replace('\n', ' ');
                            records.Add(new(timestamp, Field("level"), Field("module"), Field("event"),
                                summary.Length > 240 ? summary[..240] : summary, json));
                        }
                        catch (JsonException) { skipped++; }
                    }
                    lineStart = i + 1;
                }
                // Do not parse the unfinished final line while RuntimeLog is appending to it.
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { unavailable++; }
        }
        return new(records.OrderByDescending(record => record.Timestamp).Take(MaximumRecords).ToArray(), skipped, unavailable);
    }
}
