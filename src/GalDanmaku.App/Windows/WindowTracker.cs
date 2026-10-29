using System.Windows.Threading;

namespace GalDanmaku.App.Windows;

public sealed class WindowTracker : IDisposable
{
    private readonly DispatcherTimer _timer;
    private WindowTarget? _target;
    private WindowSnapshot _snapshot = new(null, TargetState.Unbound, default);
    private bool _disposed;

    public WindowTracker()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _timer.Tick += OnTick;
    }

    public event EventHandler<WindowSnapshot>? Changed;
    public event EventHandler<WindowSnapshot>? LayerUpdated;

    public void Bind(WindowTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        if (!MatchesIdentity(target))
        {
            throw new InvalidOperationException("所选窗口已关闭，请刷新列表后重试。");
        }

        _target = target;
        _timer.Start();
        Poll();
    }

    public void Unbind()
    {
        _timer.Stop();
        _target = null;
        Publish(new WindowSnapshot(null, TargetState.Unbound, default));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _target = null;
        Changed = null;
        LayerUpdated = null;
        _disposed = true;
    }

    private void OnTick(object? sender, EventArgs args) => Poll();

    private void Poll()
    {
        if (_target is not { } target)
        {
            return;
        }

        if (!MatchesIdentity(target))
        {
            _target = null;
            _timer.Stop();
            Publish(new WindowSnapshot(target, TargetState.Closed, default));
            return;
        }

        nint foreground = NativeMethods.GetForegroundWindow();
        TargetState state = NativeMethods.IsIconic(target.Handle) ? TargetState.Minimized
            : !NativeMethods.IsWindowVisible(target.Handle) || NativeMethods.IsCloaked(target.Handle) ? TargetState.Hidden
            : foreground != target.Handle ? TargetState.NotForeground
            : TargetState.Visible;

        if (state is not (TargetState.Visible or TargetState.NotForeground))
        {
            Publish(new WindowSnapshot(target, state, default, foreground));
            return;
        }

        // PerMonitorV2 keeps both endpoints in physical screen pixels, even for DPI-unaware targets.
        if (!NativeMethods.GetClientRect(target.Handle, out NativeMethods.NativeRectangle rectangle))
        {
            Publish(new WindowSnapshot(target, TargetState.Unavailable, default, foreground));
            return;
        }

        var origin = new NativeMethods.NativePoint { X = rectangle.Left, Y = rectangle.Top };
        var end = new NativeMethods.NativePoint { X = rectangle.Right, Y = rectangle.Bottom };
        if (!NativeMethods.ClientToScreen(target.Handle, ref origin) ||
            !NativeMethods.ClientToScreen(target.Handle, ref end) || end.X <= origin.X || end.Y <= origin.Y)
        {
            Publish(new WindowSnapshot(target, TargetState.Unavailable, default, foreground));
            return;
        }

        var snapshot = new WindowSnapshot(target, state,
            new ClientBounds(origin.X, origin.Y, end.X - origin.X, end.Y - origin.Y), foreground);
        Publish(snapshot);
        // Z order can change without geometry or foreground changing. Keep Changed's equality semantics.
        LayerUpdated?.Invoke(this, snapshot);
    }

    private void Publish(WindowSnapshot snapshot)
    {
        if (snapshot == _snapshot)
        {
            return;
        }

        _snapshot = snapshot;
        Changed?.Invoke(this, snapshot);
    }

    private static bool MatchesIdentity(WindowTarget target) =>
        NativeMethods.IsWindow(target.Handle) &&
        NativeMethods.GetWindowThreadProcessId(target.Handle, out uint processId) == target.ThreadId &&
        processId == target.ProcessId;
}
