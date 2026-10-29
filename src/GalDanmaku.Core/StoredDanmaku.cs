using System.Globalization;
using System.Text;

namespace GalDanmaku.Core;

public enum DanmakuType { Scrolling }

public sealed record StoredDanmaku
{
    public const int MaximumTextLength = 500;

    public StoredDanmaku(Guid id, string gameId, string nodeId, string text, DanmakuType type,
        string color, double fontSize, long offsetMilliseconds, DateTimeOffset createdUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("弹幕 ID 不能为空。", nameof(id));
        }

        ValidateIdentity(gameId, nameof(gameId));
        ValidateIdentity(nodeId, nameof(nodeId));
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumTextLength || text.Any(character => char.IsControl(character) ||
                char.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) ||
            !text.EnumerateRunes().Any(rune => !Rune.IsWhiteSpace(rune) && Rune.GetUnicodeCategory(rune) is not
                (UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)))
        {
            throw new ArgumentException("请输入 1–500 字的单行弹幕，不能只包含空白或控制字符。", nameof(text));
        }

        if (type != DanmakuType.Scrolling)
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        if (color is null || color.Length != 7 || color[0] != '#' || color.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF".AsSpan()))
        {
            throw new ArgumentException("颜色必须使用 #RRGGBB 格式。", nameof(color));
        }

        if (!double.IsFinite(fontSize) || fontSize < 12 || fontSize > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize), "字号必须在 12–64 之间。");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offsetMilliseconds);
        Id = id;
        GameId = gameId;
        NodeId = nodeId;
        Text = text;
        Type = type;
        Color = color.ToUpperInvariant();
        FontSize = fontSize;
        OffsetMilliseconds = offsetMilliseconds;
        CreatedUtc = createdUtc.ToUniversalTime();
    }
    public Guid Id { get; }
    public string GameId { get; } = "";
    public string NodeId { get; } = "";
    public string Text { get; } = "";
    public DanmakuType Type { get; }
    public string Color { get; } = "";
    public double FontSize { get; }
    public long OffsetMilliseconds { get; }
    public DateTimeOffset CreatedUtc { get; }

    internal static void ValidateIdentity(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.Any(char.IsControl))
        {
            throw new ArgumentException("标识长度不能超过 128，且不能包含控制字符。", parameterName);
        }
    }
}
