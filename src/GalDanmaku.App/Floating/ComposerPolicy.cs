namespace GalDanmaku.App.Floating;

// Physical screen pixels are used here; WPF DIP conversion belongs at the window boundary.
public readonly record struct ComposerRectangle(double X, double Y, double Width, double Height);
public readonly record struct ComposerOffset(double X, double Y);

public static class ComposerPolicy
{
    public static bool IsVisible(bool targetForeground, bool targetAvailable, bool composerForeground) =>
        targetAvailable && (targetForeground || composerForeground);

    public static ComposerRectangle Place(ComposerRectangle target, ComposerRectangle workArea,
        double width, double height, double gap, ComposerOffset? offset = null)
    {
        width = Math.Clamp(width, 1, Math.Max(1, workArea.Width));
        height = Math.Clamp(height, 1, Math.Max(1, workArea.Height));
        double x = offset is { } relative ? target.X + relative.X : target.X + (target.Width - width) / 2;
        double y = offset is { } relativeY ? target.Y + relativeY.Y : target.Y + target.Height + gap;
        return new ComposerRectangle(
            Math.Clamp(x, workArea.X, workArea.X + Math.Max(0, workArea.Width - width)),
            Math.Clamp(y, workArea.Y, workArea.Y + Math.Max(0, workArea.Height - height)), width, height);
    }
}
