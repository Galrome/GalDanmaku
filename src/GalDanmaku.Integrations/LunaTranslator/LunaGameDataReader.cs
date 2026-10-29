using System.Text.Json;

namespace GalDanmaku.Integrations.LunaTranslator;

// Reads only identity fields. Other Luna settings are neither retained nor logged.
internal static class LunaGameDataReader
{
    internal const string FileName = "savegamedata_5.3.1.json";
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumRecords = 20000;

    internal static string ResolveFilePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase)) return fullPath;
        string direct = Path.Combine(fullPath, FileName);
        return File.Exists(direct) || string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath)),
            "userconfig", StringComparison.OrdinalIgnoreCase)
            ? direct : Path.Combine(fullPath, "userconfig", FileName);
    }

    internal static async Task<LunaGameDataSnapshot> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        // Delete sharing permits Luna to atomically replace its file while this handle reads a snapshot.
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Luna 游戏库超过 16 MiB 限制。");
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaximumFileBytes) throw new InvalidDataException("Luna 游戏库超过 16 MiB 限制。");
            buffer.Write(chunk, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> json = buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length));
        if (json.Length >= 3 && json.Span[0] == 0xef && json.Span[1] == 0xbb && json.Span[2] == 0xbf) json = json[3..];
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2
            || root[0].ValueKind != JsonValueKind.Array || root[1].ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Luna 游戏库结构不受支持，需要 UID 列表和游戏详情表。");

        // Details can retain deleted/historical records. Only current list/group references form the library.
        var records = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var duplicateUids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root[1].EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (records.Count >= MaximumRecords) throw new InvalidDataException("Luna 游戏库记录超过 20000 条限制。");
            if (!records.TryAdd(property.Name, property.Value)) duplicateUids.Add(property.Name);
        }
        var orderedUids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement uid in root[0].EnumerateArray())
            if (uid.ValueKind == JsonValueKind.String && uid.GetString() is { } key && seen.Add(key)) orderedUids.Add(key);
        if (root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.Array)
            foreach (JsonElement group in root[2].EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("games", out JsonElement groupGames)
                    || groupGames.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement uid in groupGames.EnumerateArray())
                    if (uid.ValueKind == JsonValueKind.String && uid.GetString() is { } key && seen.Add(key)) orderedUids.Add(key);
            }

        string configDirectory = Path.GetDirectoryName(filePath)!;
        string gameBase = string.Equals(Path.GetFileName(configDirectory), "userconfig", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(configDirectory)! : configDirectory;
        var games = new List<LunaGameRecord>();
        int skipped = 0;
        foreach (string uid in orderedUids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!records.TryGetValue(uid, out JsonElement record)) continue;
            if (string.IsNullOrWhiteSpace(uid) || uid.Length > 512 || duplicateUids.Contains(uid)
                || record.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                continue;
            }
            string? path = NormalizePath(ReadString(record, "gamepath", 32767), gameBase);
            if (path is null)
            {
                skipped++;
                continue;
            }
            string title = ReadString(record, "title", 1024) ?? Path.GetFileNameWithoutExtension(path);
            games.Add(new LunaGameRecord(uid, path, title,
                ReadIdentifier(record, "vid", vndb: true), ReadIdentifier(record, "bgmsid", vndb: false),
                NormalizePath(ReadString(record, "launchpath", 32767), gameBase), ReadScalar(record, "launch_method", 128)));
        }
        return new LunaGameDataSnapshot(games.AsReadOnly(), skipped, records.Keys.Count(uid => !seen.Contains(uid)));
    }

    private static string? ReadString(JsonElement record, string key, int maximumLength)
    {
        if (!record.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.String) return null;
        string? text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) || text.Length > maximumLength || text.Any(char.IsControl) ? null : text;
    }

    private static string? ReadScalar(JsonElement record, string key, int maximumLength)
    {
        if (!record.TryGetProperty(key, out JsonElement value)) return null;
        if (value.ValueKind == JsonValueKind.String) return ReadString(record, key, maximumLength);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static string? ReadIdentifier(JsonElement record, string key, bool vndb)
    {
        string? value = ReadScalar(record, key, 32);
        if (vndb && value?.StartsWith('v') == true) value = value[1..];
        if (string.IsNullOrEmpty(value) || !value.All(char.IsAsciiDigit)
            || !ulong.TryParse(value, out ulong number) || number == 0) return null;
        string digits = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return vndb ? "v" + digits : digits;
    }

    private static string? NormalizePath(string? path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path, baseDirectory); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }
}
