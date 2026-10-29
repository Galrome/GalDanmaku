namespace GalDanmaku.App.Windows;

public sealed record WindowTarget(nint Handle, uint ProcessId, uint ThreadId, string ProcessName, string Title)
{
    public string Details => $"{ProcessName} · PID {ProcessId} · HWND 0x{Handle:X}";
}

public readonly record struct ClientBounds(int X, int Y, int Width, int Height);

public enum TargetState
{
    Unbound,
    Visible,
    NotForeground,
    Minimized,
    Hidden,
    Unavailable,
    Closed,
}

public sealed record WindowSnapshot(WindowTarget? Target, TargetState State, ClientBounds Bounds, nint ForegroundHandle = default);
