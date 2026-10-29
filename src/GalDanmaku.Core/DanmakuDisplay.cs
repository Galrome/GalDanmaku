using System.Globalization;
using System.Text;

namespace GalDanmaku.Core;

// A presentation of saved comments. It never replaces or changes the original records.
public sealed class DanmakuDisplay
{
    internal DanmakuDisplay(string displayId, StoredDanmaku representative, int count, long offsetMilliseconds)
    {
        DisplayId = displayId;
        Representative = representative;
        Count = count;
        OffsetMilliseconds = offsetMilliseconds;
        Text = count > 1 ? $"{representative.Text} ×{count.ToString(CultureInfo.InvariantCulture)}" : representative.Text;
        FontSize = count > 1
            ? Math.Min(64, DanmakuSettings.DefaultFontSize * (1 + Math.Min(0.75, 0.15 * Math.Log2(count))))
            : representative.FontSize;
    }

    public string DisplayId { get; }
    public string Text { get; }
    public double FontSize { get; }
    public string Color => Representative.Color;
    public long OffsetMilliseconds { get; }
    public int Count { get; }
    public bool IsAggregate => Count > 1;
    public bool CanLike => !IsAggregate;
    // Aggregates deliberately expose no individual target for like actions.
    public StoredDanmaku? Source => IsAggregate ? null : Representative;
    internal StoredDanmaku Representative { get; }
}

internal static class DanmakuAggregation
{
    public static string DisplayId(StoredDanmaku comment, bool aggregate)
    {
        if (!aggregate) return "comment:" + comment.Id.ToString("D");
        string text = NormalizeWhitespace(comment.Text);
        // Length prefixes make identities unambiguous even when IDs contain punctuation.
        return $"group:{comment.GameId.Length}:{comment.GameId}{comment.NodeId.Length}:{comment.NodeId}{(int)comment.Type}:{text}";
    }

    private static string NormalizeWhitespace(string text)
    {
        var normalized = new StringBuilder(text.Length);
        bool whitespace = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                whitespace = normalized.Length > 0;
                continue;
            }

            if (whitespace) normalized.Append(' ');
            normalized.Append(rune.ToString());
            whitespace = false;
        }

        return normalized.ToString();
    }

    public static IOrderedEnumerable<StoredDanmaku> Ordered(IEnumerable<StoredDanmaku> comments) =>
        comments.OrderBy(comment => comment.OffsetMilliseconds).ThenBy(comment => comment.CreatedUtc.UtcTicks).ThenBy(comment => comment.Id);
}
