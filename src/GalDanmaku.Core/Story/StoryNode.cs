using GalDanmaku.Core.Text;

namespace GalDanmaku.Core.Story;

public sealed class StoryNode
{
    public string Id { get; }
    public string Text { get; }
    public string NormalizedText { get; }
    public IReadOnlyList<string> NextIds { get; }

    public StoryNode(string id, string text, IEnumerable<string> nextIds)
    {
        StoryIndex.ValidateId(id);
        if (text is null || text.Length > DialogueProcessor.MaximumTextLength)
            throw new InvalidDataException("剧情文本为空或超过长度限制。");
        try { NormalizedText = TextNormalizer.Normalize(text); }
        catch (ArgumentException exception) { throw new InvalidDataException("剧情文本包含无效字符。", exception); }
        if (NormalizedText.Length == 0) throw new InvalidDataException("剧情文本归一化后不能为空。");
        if (nextIds is null) throw new InvalidDataException("后继节点列表不能为空。");
        var edges = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string edge in nextIds)
        {
            StoryIndex.ValidateId(edge);
            if (edges.Count == StoryIndex.MaximumEdgesPerNode || !unique.Add(edge))
                throw new InvalidDataException("后继节点过多或重复。");
            edges.Add(edge);
        }
        Id = id;
        Text = text;
        NextIds = edges.AsReadOnly();
    }
}
