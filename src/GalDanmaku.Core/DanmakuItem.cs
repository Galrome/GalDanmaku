namespace GalDanmaku.Core;

public sealed class DanmakuItem
{
    internal DanmakuItem(string text, double x, double y, double width)
    {
        Text = text;
        X = x;
        Y = y;
        Width = width;
    }

    public string Text { get; }
    public double X { get; internal set; }
    public double Y { get; }
    public double Width { get; }
}
