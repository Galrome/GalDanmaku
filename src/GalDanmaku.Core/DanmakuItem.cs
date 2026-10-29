namespace GalDanmaku.Core;

public sealed class DanmakuItem
{
    internal DanmakuItem(string text, double x, double y, double width, double fontSize, string color, bool usesDefaultFontSize)
    {
        Text = text;
        X = x;
        Y = y;
        Width = width;
        FontSize = fontSize;
        Color = color;
        UsesDefaultFontSize = usesDefaultFontSize;
    }

    public string Text { get; }
    public double X { get; internal set; }
    public double Y { get; }
    public double Width { get; }
    public double FontSize { get; }
    public string Color { get; } = "#FFFFFF";
    internal bool UsesDefaultFontSize { get; }
}
