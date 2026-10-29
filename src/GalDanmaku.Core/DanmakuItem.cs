namespace GalDanmaku.Core;

public sealed class DanmakuItem
{
    internal DanmakuItem(string text, double x, double y, double width, double fontSize, string color, bool usesDefaultFontSize,
        DanmakuDisplay? display = null)
    {
        Text = text;
        X = x;
        Y = y;
        Width = width;
        FontSize = fontSize;
        Color = color;
        UsesDefaultFontSize = usesDefaultFontSize;
        Display = display;
    }

    public string Text { get; }
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double Width { get; internal set; }
    // Retained while a temporary viewport cannot place this already emitted visual.
    public bool IsLayoutVisible { get; internal set; } = true;
    public double FontSize { get; }
    public string Color { get; } = "#FFFFFF";
    public DanmakuDisplay? Display { get; }
    internal bool UsesDefaultFontSize { get; }
}
