using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GalDanmaku.App.Windows;
using GalDanmaku.Core;
using GalDanmaku.Core.Diagnostics;

namespace GalDanmaku.App.Overlay;

public sealed class OverlayWindow : Window
{
    private readonly OverlayRenderer _renderer;
    private bool _playbackEnabled;
    private bool _targetForeground;
    private HwndSource? _source;
    private nint _handle;
    private readonly HoverActionWindow _actions;
    private WindowTarget? _target;
    private long? _outsideSince;
    private double _actionAnchorOffset;
    private long? _lastPlacementError;
    private long _suppressedPlacementErrors;

    public event EventHandler? HoveredCommentChanged;
    public event EventHandler? LikeRequested;
    public StoredDanmaku? HoveredComment => _renderer.HoveredComment;

    public OverlayWindow(NodePlayback playback)
    {
        _renderer = new OverlayRenderer(playback);
        _actions = new HoverActionWindow();
        _renderer.FrameAdvanced += OnFrameAdvanced;
        _renderer.HoveredCommentChanged += OnHoveredCommentChanged;
        _actions.ActionRequested += OnLikeRequested;
        Title = "GalDanmaku Overlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        Width = 1;
        Height = 1;
        Content = _renderer;
        RuntimeLog.Write(LogLevel.Information, "Overlay", "OverlayCreated");
    }

    public DanmakuSettings Settings
    {
        get => _renderer.Settings;
        set => _renderer.Settings = value;
    }

    public bool PlaybackEnabled
    {
        get => _playbackEnabled;
        set
        {
            _playbackEnabled = value;
            ApplyPlaybackState();
        }
    }

    public void ResetVisuals() => _renderer.ResetVisuals();

    public void Follow(ClientBounds bounds, WindowTarget target, bool isForeground = true)
    {
        if (_target != target) _renderer.ClearHover();
        _target = target;
        _targetForeground = isForeground;
        _handle = new WindowInteropHelper(this).EnsureHandle();
        Position(bounds);
        if (!IsVisible)
        {
            Show();
            RuntimeLog.Write(LogLevel.Information, "Overlay", "OverlayShown", new { hwnd = $"0x{target.Handle:X}", pid = target.ProcessId });
            // WPF can reapply its cached location during Show; native pixels remain authoritative.
            Position(bounds);
        }
        // WPF rejects an Owner whose HWND has not been created yet.
        if (_actions.Owner is null) _actions.Owner = this;

        SyncZOrder(target);
    }

    private void ApplyPlaybackState()
    {
        if (_playbackEnabled && _targetForeground && IsVisible && TargetStillForeground()) _renderer.Start();
        else _renderer.Stop();
    }

    public void SyncZOrder(WindowTarget target)
    {
        // Changed handlers can unbind before the poll's LayerUpdated notification arrives.
        if (!IsVisible || _target != target) return;
        if (!NativeMethods.IsWindow(target.Handle) ||
            NativeMethods.GetWindowThreadProcessId(target.Handle, out uint processId) != target.ThreadId || processId != target.ProcessId)
            throw new Win32Exception("目标窗口已关闭，无法同步覆盖层层级。");
        WindowLayer.PlaceAboveTarget(_handle, target.Handle);
        // A setter may have stopped playback during a foreground change shorter than one tracker poll.
        // Recheck on every valid poll, including those that keep the same snapshot.
        ApplyPlaybackState();
    }

    public void Suspend()
    {
        if (IsVisible) RuntimeLog.Write(LogLevel.Information, "Overlay", "OverlayHidden", new { reason = "TargetUnavailableOrUnbound" });
        _targetForeground = false;
        _renderer.Stop();
        Hide();
    }

    public void UpdateLikeState(bool? liked, bool busy, string? error) => _actions.UpdateState(liked, busy, error);

    private void OnLikeRequested(object? sender, EventArgs args)
    {
        if (TargetStillForeground() && HoveredComment is not null) LikeRequested?.Invoke(this, EventArgs.Empty);
        else _renderer.ClearHover();
    }

    private bool TargetStillForeground() => _target is { } target &&
        NativeMethods.GetForegroundWindow() == target.Handle && NativeMethods.IsWindowVisible(target.Handle) &&
        !NativeMethods.IsIconic(target.Handle) && !NativeMethods.IsCloaked(target.Handle) &&
        NativeMethods.GetWindowThreadProcessId(target.Handle, out uint processId) == target.ThreadId && processId == target.ProcessId;

    private void OnFrameAdvanced(object? sender, EventArgs args)
    {
        if (!IsVisible || !TargetStillForeground() || !NativeMethods.GetCursorPos(out NativeMethods.NativePoint cursor))
        {
            _renderer.ClearHover();
            return;
        }
        var screenPoint = new Point(cursor.X, cursor.Y);
        if (HoveredComment is null)
        {
            _renderer.HoverAt(_renderer.PointFromScreen(screenPoint));
            return;
        }

        try
        {
            Rect textBounds = ScreenBounds(_renderer.HoveredBounds);
            _actions.Place(textBounds, ScreenBounds(new Rect(_renderer.RenderSize)), textBounds.Left + _actionAnchorOffset);
            Rect keepText = textBounds;
            Rect keepActions = _actions.ScreenBounds;
            // A few physical pixels bridge rounded corners and fractional DIP rounding.
            keepText.Inflate(4, 4);
            keepActions.Inflate(4, 4);
            bool retained = keepText.Contains(screenPoint) || keepActions.Contains(screenPoint) || _actions.HasPointerCapture;
            if (retained) _outsideSince = null;
            else
            {
                _outsideSince ??= Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(_outsideSince.Value).TotalMilliseconds >= 100) _renderer.ClearHover();
            }
        }
        catch (Win32Exception exception)
        {
            LogPlacementFailure(exception);
            // A failed action window must never leave a stationary item or an invisible input surface.
            _renderer.ClearHover();
        }
    }

    private Rect ScreenBounds(Rect bounds) => bounds.IsEmpty ? Rect.Empty :
        new Rect(_renderer.PointToScreen(bounds.TopLeft), _renderer.PointToScreen(bounds.BottomRight));

    private void OnHoveredCommentChanged(object? sender, EventArgs args)
    {
        _outsideSince = null;
        if (HoveredComment is null) _actions.Suspend();
        else
        {
            _actions.UpdateState(null, busy: true, error: null);
            Rect textBounds = ScreenBounds(_renderer.HoveredBounds);
            _actionAnchorOffset = NativeMethods.GetCursorPos(out NativeMethods.NativePoint cursor)
                ? Math.Clamp(cursor.X - textBounds.Left, 0, textBounds.Width) : textBounds.Width / 2;
            try { _actions.Place(textBounds, ScreenBounds(new Rect(_renderer.RenderSize)), textBounds.Left + _actionAnchorOffset); }
            catch (Win32Exception exception)
            {
                LogPlacementFailure(exception);
                _renderer.ClearHover(); return;
            }
        }
        HoveredCommentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LogPlacementFailure(Exception exception)
    {
        if (_lastPlacementError is { } last && Stopwatch.GetElapsedTime(last).TotalSeconds < 5)
        {
            _suppressedPlacementErrors++;
            return;
        }
        RuntimeLog.Write(LogLevel.Warning, "Overlay", "HoverActionPlacementFailed", new { suppressedSinceLast = _suppressedPlacementErrors }, exception);
        _lastPlacementError = Stopwatch.GetTimestamp();
        _suppressedPlacementErrors = 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        long style = (long)NativeMethods.GetExtendedStyle(_handle);
        style = (style | NativeMethods.LayeredStyle | NativeMethods.TransparentStyle |
            NativeMethods.NoActivateStyle | NativeMethods.ToolWindowStyle) & ~NativeMethods.AppWindowStyle;
        Marshal.SetLastPInvokeError(0);
        nint previous = NativeMethods.SetExtendedStyle(_handle, (nint)style);
        int error = Marshal.GetLastWin32Error();
        if (previous == nint.Zero && error != 0)
        {
            RuntimeLog.Write(LogLevel.Error, "Overlay", "OverlayNativeInitializationFailed", new { win32Error = error });
            throw new Win32Exception(error, "无法配置鼠标穿透覆盖层。");
        }

        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowProcedure);
        RuntimeLog.Write(LogLevel.Information, "Overlay", "OverlayNativeInitialized", new { hwnd = $"0x{_handle:X}", mouseTransparent = true });
    }

    protected override void OnClosed(EventArgs e)
    {
        _renderer.Dispose();
        _renderer.FrameAdvanced -= OnFrameAdvanced;
        _renderer.HoveredCommentChanged -= OnHoveredCommentChanged;
        _actions.ActionRequested -= OnLikeRequested;
        _actions.Shutdown();
        _source?.RemoveHook(WindowProcedure);
        _source = null;
        RuntimeLog.Write(LogLevel.Information, "Overlay", "OverlayDestroyed");
        base.OnClosed(e);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        RuntimeLog.Write(LogLevel.Debug, "Overlay", "OverlayDpiChanged", new { previousX = oldDpi.DpiScaleX, previousY = oldDpi.DpiScaleY, currentX = newDpi.DpiScaleX, currentY = newDpi.DpiScaleY });
        _renderer.ClearHover();
        base.OnDpiChanged(oldDpi, newDpi);
    }

    private void Position(ClientBounds bounds)
    {
        if (!NativeMethods.SetWindowPos(_handle, nint.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            NativeMethods.NoActivatePosition | NativeMethods.NoOwnerZOrderPosition | NativeMethods.NoZOrderPosition))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法定位覆盖层。");
        }
    }

    private static nint WindowProcedure(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int MouseActivate = 0x0021;
        const int NonClientHitTest = 0x0084;
        if (message == MouseActivate)
        {
            handled = true;
            return new nint(3); // MA_NOACTIVATE is a fallback; WS_EX_NOACTIVATE is the primary protection.
        }

        if (message == NonClientHitTest)
        {
            handled = true;
            return new nint(-1); // Layered + WS_EX_TRANSPARENT also passes hits to other processes.
        }

        return nint.Zero;
    }
}
