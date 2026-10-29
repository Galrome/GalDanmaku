using System.Text.Json;
using GalDanmaku.Core.Story;
using GalDanmaku.Core.Text;

namespace GalDanmaku.Core.GamePacks;

internal sealed record ParsedManifest(GamePack Pack, string StoryIndexPath);

// Strict parser: unknown fields, duplicate keys and wrong types are all rejected with InvalidDataException.
internal static class GamePackManifest
{
    public const int MaximumBytes = 64 * 1024;
    public const int MaximumProcessNames = 8;
    public const int MaximumRemoveStrings = 32;
    public const int MaximumRemoveStringLength = 64;
    public const int MaximumMockScriptFrames = 500;

    public static async Task<ParsedManifest> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        string root = GamePackFiles.NormalizeDirectory(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("游戏包目录不存在。");
        string path = GamePackFiles.ResolveInside(root, GamePackFiles.ManifestFileName);
        if (!File.Exists(path)) throw new FileNotFoundException("游戏包缺少 gamepack.json。", path);
        string json = await GamePackFiles.ReadUtf8Async(path, MaximumBytes, "游戏包清单", cancellationToken).ConfigureAwait(false);
        return Parse(json, root);
    }

    public static ParsedManifest Parse(string json, string root)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var fields = ReadObject(document.RootElement, "清单",
                "SchemaVersion", "GameId", "Name", "Version", "Engine", "Identification", "StoryIndex", "TextSource", "Normalization");
            if (ReadInt(Require(fields, "SchemaVersion"), "SchemaVersion") != 1) throw Invalid("不支持此游戏包清单版本。");

            string gameId = ReadString(Require(fields, "GameId"), "GameId");
            StoryIndex.ValidateId(gameId);
            try { StoredDanmaku.ValidateIdentity(gameId, nameof(gameId)); }
            catch (ArgumentException exception) { throw new InvalidDataException("GameId 不符合存储标识规则。", exception); }

            string name = ReadText(Require(fields, "Name"), "Name", 1, 100);
            string version = ReadText(Require(fields, "Version"), "Version", 1, 32);
            string? engine = fields.TryGetValue("Engine", out JsonElement engineElement) ? ReadText(engineElement, "Engine", 1, 64) : null;
            string storyIndex = ReadString(Require(fields, "StoryIndex"), "StoryIndex");
            GamePackFiles.ResolveInside(root, storyIndex);

            string[] processNames = [];
            if (fields.TryGetValue("Identification", out JsonElement identification))
            {
                var identificationFields = ReadObject(identification, "Identification", "ProcessNames");
                if (identificationFields.TryGetValue("ProcessNames", out JsonElement names))
                    processNames = ReadProcessNames(names);
            }

            string[] removeStrings = [];
            if (fields.TryGetValue("Normalization", out JsonElement normalization))
            {
                var normalizationFields = ReadObject(normalization, "Normalization", "RemoveStrings");
                if (normalizationFields.TryGetValue("RemoveStrings", out JsonElement remove))
                    removeStrings = ReadStringArray(remove, "RemoveStrings", MaximumRemoveStrings, 1, MaximumRemoveStringLength);
            }

            GamePackTextSource textSource = ReadTextSource(Require(fields, "TextSource"));
            var pack = new GamePack(root, gameId, name, version, engine, Array.AsReadOnly(processNames), textSource, removeStrings);
            return new ParsedManifest(pack, storyIndex);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("游戏包清单 JSON 格式无效。", exception);
        }
    }

    private static GamePackTextSource ReadTextSource(JsonElement element)
    {
        var fields = ReadObject(element, "TextSource", "Kind", "Endpoint", "InitialRetryMilliseconds",
            "MaximumRetryMilliseconds", "ConnectTimeoutMilliseconds", "MergeGapMilliseconds", "MockScript");
        GamePackTextSourceKind kind = ReadString(Require(fields, "Kind"), "Kind") switch
        {
            "Mock" => GamePackTextSourceKind.Mock,
            "Luna" => GamePackTextSourceKind.Luna,
            _ => throw Invalid("TextSource.Kind 只能是 Luna 或 Mock。"),
        };
        var defaults = new TextSourceOptions();
        var options = new TextSourceOptions
        {
            Endpoint = fields.TryGetValue("Endpoint", out JsonElement endpoint) ? ReadString(endpoint, "Endpoint") : defaults.Endpoint,
            InitialRetryMilliseconds = fields.TryGetValue("InitialRetryMilliseconds", out JsonElement initial)
                ? ReadInt(initial, "InitialRetryMilliseconds") : defaults.InitialRetryMilliseconds,
            MaximumRetryMilliseconds = fields.TryGetValue("MaximumRetryMilliseconds", out JsonElement maximum)
                ? ReadInt(maximum, "MaximumRetryMilliseconds") : defaults.MaximumRetryMilliseconds,
            ConnectTimeoutMilliseconds = fields.TryGetValue("ConnectTimeoutMilliseconds", out JsonElement timeout)
                ? ReadInt(timeout, "ConnectTimeoutMilliseconds") : defaults.ConnectTimeoutMilliseconds,
            MergeGapMilliseconds = fields.TryGetValue("MergeGapMilliseconds", out JsonElement gap)
                ? ReadInt(gap, "MergeGapMilliseconds") : defaults.MergeGapMilliseconds,
        };
        try { options.Validate(); }
        catch (ArgumentException exception) { throw new InvalidDataException("文本源配置无效：" + exception.Message, exception); }

        string[] script = [];
        if (fields.TryGetValue("MockScript", out JsonElement scriptElement))
        {
            if (kind != GamePackTextSourceKind.Mock) throw Invalid("MockScript 仅在 Kind 为 Mock 时有效。");
            script = ReadStringArray(scriptElement, "MockScript", MaximumMockScriptFrames, 0, DialogueProcessor.MaximumTextLength);
        }
        return new GamePackTextSource(kind, options, Array.AsReadOnly(script));
    }

    private static string[] ReadProcessNames(JsonElement element)
    {
        string[] names = ReadStringArray(element, "ProcessNames", MaximumProcessNames, 1, 128);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (string name in names)
        {
            // File name only: any separator, drive colon or invalid character is a path in disguise.
            if (name.IndexOfAny(invalid) >= 0 || name.Contains('/') || name.Contains('\\') || name.Contains(':')
                || name.Trim() != name || name is "." or "..")
                throw Invalid("进程名只能是文件名，不能包含路径或无效字符。");
            if (!seen.Add(name)) throw Invalid("进程名重复。");
        }
        return names;
    }

    private static string[] ReadStringArray(JsonElement element, string field, int maximumCount, int minimumLength, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.Array) throw Invalid($"{field} 必须是数组。");
        if (element.GetArrayLength() > maximumCount) throw Invalid($"{field} 超过 {maximumCount} 项限制。");
        var result = new List<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            string value = ReadString(item, field);
            if (value.Length < minimumLength || value.Length > maximumLength)
                throw Invalid($"{field} 的每一项须为 {minimumLength}–{maximumLength} 个字符。");
            result.Add(value);
        }
        return result.ToArray();
    }

    private static Dictionary<string, JsonElement> ReadObject(JsonElement element, string what, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid($"{what}必须是 JSON 对象。");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw Invalid($"{what}包含未知字段：{property.Name}。");
            if (!fields.TryAdd(property.Name, property.Value)) throw Invalid($"{what}包含重复字段：{property.Name}。");
        }
        return fields;
    }

    private static JsonElement Require(Dictionary<string, JsonElement> fields, string name)
        => fields.TryGetValue(name, out JsonElement value) ? value : throw Invalid($"缺少必填字段：{name}。");

    private static string ReadString(JsonElement element, string field)
        => element.ValueKind == JsonValueKind.String ? element.GetString()! : throw Invalid($"{field} 必须是字符串。");

    private static int ReadInt(JsonElement element, string field)
        => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int value) ? value : throw Invalid($"{field} 必须是整数。");

    private static string ReadText(JsonElement element, string field, int minimum, int maximum)
    {
        string value = ReadString(element, field);
        if (value.Length < minimum || value.Length > maximum || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw Invalid($"{field} 须为 {minimum}–{maximum} 个字符，不能只含空白或控制字符。");
        return value;
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
