using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace GalDanmaku.Core.Story;

public sealed class StoryIndex
{
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    public const int MaximumNodes = 10000;
    public const int MaximumEdgesPerNode = 64;
    public const int MaximumTotalEdges = 40000;
    public const int MaximumTotalTextLength = 1024 * 1024;
    private static readonly IReadOnlyList<StoryNode> EmptyCandidates = Array.AsReadOnly(Array.Empty<StoryNode>());
    private readonly Dictionary<string, IReadOnlyList<StoryNode>> _byText;
    private readonly string[] _sortedTexts;

    public int SchemaVersion => 1;
    public string GameId { get; }
    public IReadOnlyDictionary<string, StoryNode> Nodes { get; }

    public StoryIndex(int schemaVersion, string gameId, IEnumerable<StoryNode> nodes)
    {
        if (schemaVersion != 1) throw new InvalidDataException("不支持此剧情索引版本。");
        ValidateId(gameId);
        if (nodes is null) throw new InvalidDataException("节点列表不能为空。");
        var owned = new Dictionary<string, StoryNode>(StringComparer.Ordinal);
        int textLength = 0;
        int edgeCount = 0;
        foreach (StoryNode node in nodes)
        {
            if (node is null || owned.Count == MaximumNodes || !owned.TryAdd(node.Id, node))
                throw new InvalidDataException("节点为空、重复或超过数量限制。");
            textLength += node.Text.Length;
            edgeCount += node.NextIds.Count;
            if (textLength > MaximumTotalTextLength || edgeCount > MaximumTotalEdges)
                throw new InvalidDataException("剧情索引文本或连接总量超过限制。");
        }
        if (owned.Count == 0) throw new InvalidDataException("剧情索引至少需要一个节点。");
        foreach (StoryNode node in owned.Values)
            if (node.NextIds.Any(id => !owned.ContainsKey(id))) throw new InvalidDataException("后继节点不存在。");
        GameId = gameId;
        Nodes = new ReadOnlyDictionary<string, StoryNode>(owned);
        _byText = owned.Values.GroupBy(node => node.NormalizedText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<StoryNode>)Array.AsReadOnly(group.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray()), StringComparer.Ordinal);
        _sortedTexts = _byText.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    // Both lookups take text already normalized by the shared TextNormalizer.
    public IReadOnlyList<StoryNode> FindExact(string normalizedText)
        => _byText.GetValueOrDefault(normalizedText, EmptyCandidates);

    public IReadOnlyList<StoryNode> FindPrefix(string normalizedText)
    {
        if (normalizedText.Length == 0) return EmptyCandidates;
        int start = Array.BinarySearch(_sortedTexts, normalizedText, StringComparer.Ordinal);
        if (start < 0) start = ~start;
        var candidates = new List<StoryNode>();
        for (int i = start; i < _sortedTexts.Length && _sortedTexts[i].StartsWith(normalizedText, StringComparison.Ordinal); i++)
            candidates.AddRange(_byText[_sortedTexts[i]]);
        candidates.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        return candidates.AsReadOnly();
    }

    public static StoryIndex Parse(string json)
    {
        if (json is null || json.Length > MaximumFileBytes || Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
            throw new InvalidDataException("剧情索引文件为空或超过 2 MiB 限制。");
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            int version = root.GetProperty("SchemaVersion").GetInt32();
            string? gameId = root.GetProperty("GameId").GetString();
            JsonElement nodes = root.GetProperty("Nodes");
            if (nodes.GetArrayLength() > MaximumNodes) throw new InvalidDataException("剧情节点超过数量限制。");
            return new StoryIndex(version, gameId!, nodes.EnumerateArray().Select(node => new StoryNode(
                node.GetProperty("Id").GetString()!, node.GetProperty("Text").GetString()!,
                node.GetProperty("NextIds").EnumerateArray().Select(edge => edge.GetString()!))));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("剧情索引 JSON 格式无效。", exception);
        }
    }

    public static async Task<StoryIndex> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("剧情索引文件超过 2 MiB 限制。");
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            // Check actual bytes as well as Length, so growth during reading cannot bypass the limit.
            if (buffer.Length + count > MaximumFileBytes) throw new InvalidDataException("剧情索引文件超过 2 MiB 限制。");
            buffer.Write(chunk, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string json = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            return Parse(json.TrimStart('\uFEFF'));
        }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("剧情索引必须使用 UTF-8 编码。", exception); }
    }

    internal static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':')))
            throw new InvalidDataException("剧情标识须为 1–128 位 ASCII 字母、数字或 - _ . : 。");
    }
}
