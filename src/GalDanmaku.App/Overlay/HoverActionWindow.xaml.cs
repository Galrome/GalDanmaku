using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using GalDanmaku.App.Windows;

namespace GalDanmaku.App.Overlay;

public partial class HoverActionWindow : Window
{
    private HwndSource? _source;
    private nint _handle;
    private ClientBounds? _placement;
    private bool _closed;

    public HoverActionWindow() => InitializeComponent();
    public event EventHandler? ActionRequested;
    public bool HasPointerCapture => IsMouseCaptureWithin;
    public Rect ScreenBounds => _placement is { } p && IsVisible ? new Rect(p.X, p.Y, p.Width, p.Height) : Rect.Empty;

    public void UpdateState(bool? liked, bool busy, string? error)
    {
        LikeButton.IsEnabled = !busy && (liked.HasValue || error is not null);
        LikeButton.Content = busy ? liked.HasValue ? "保存中…" : "读取中…"
            : error is not null ? liked.HasValue ? "保存失败 · 重试" : "读取失败 · 重试"
            : liked == true ? "已赞 · 取消" : "点赞";
        AutomationProperties.SetName(LikeButton, error ?? (liked == true ? "取消本地点赞" : "本地点赞"));
    }

    public void Place(Rect anchor, Rect viewport, double horizontalAnchor)
    {
        if (_closed || anchor.IsEmpty) return;
        _handle = new WindowInteropHelper(this).EnsureHandle();
        Position(anchor, viewport, horizontalAnchor);
        if (!IsVisible)
        {
            Show();
            // As with the overlay, WPF can restore cached startup geometry during Show.
            _placement = null;
            Position(anchor, viewport, horizontalAnchor);
        }
    }

    public void Suspend()
    {
        if (_closed) return;
        if (IsMouseCaptureWithin) Mouse.Capture(null);
        Hide();
        _placement = null;
    }

    public void Shutdown()
    {
        if (!_closed) Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        long style = (long)NativeMethods.GetExtendedStyle(_handle);
        style = (style | NativeMethods.NoActivateStyle | NativeMethods.ToolWindowStyle) &
            ~(NativeMethods.AppWindowStyle | NativeMethods.TransparentStyle);
        Marshal.SetLastPInvokeError(0);
        nint previous = NativeMethods.SetExtendedStyle(_handle, (nint)style);
        int error = Marshal.GetLastWin32Error();
        if (previous == nint.Zero && error != 0) throw new Win32Exception(error, "无法配置弹幕操作栏。");
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowProcedure);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _placement = null;
        base.OnDpiChanged(oldDpi, newDpi);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _source?.RemoveHook(WindowProcedure);
        _source = null;
        base.OnClosed(e);
    }

    private void Position(Rect anchor, Rect viewport, double horizontalAnchor)
    {
        var monitor = new NativeMethods.MonitorInformation { Size = Marshal.SizeOf<NativeMethods.MonitorInformation>() };
        var anchorPoint = new NativeMethods.NativePoint { X = (int)horizontalAnchor, Y = (int)anchor.Top };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromPoint(anchorPoint, 2), ref monitor))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取弹幕操作栏所在显示器。");
        NativeMethods.NativeRectangle work = monitor.WorkArea;
        viewport.Intersect(new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top));
        if (viewport.IsEmpty) throw new Win32Exception("弹幕操作栏已离开可见区域。");
        double scale = Math.Max(96, NativeMethods.GetDpiForWindow(_handle)) / 96.0;
        int width = (int)Math.Ceiling(166 * scale);
        int height = (int)Math.Ceiling(38 * scale);
        double left = Math.Clamp(horizontalAnchor - width / 2.0, viewport.Left, Math.Max(viewport.Left, viewport.Right - width));
        // Touch the text rectangle so there is no dead strip between text and button.
        double top = anchor.Bottom;
        if (top + height > viewport.Bottom) top = anchor.Top - height;
        top = Math.Clamp(top, viewport.Top, Math.Max(viewport.Top, viewport.Bottom - height));
        var placement = new ClientBounds((int)Math.Floor(left), (int)Math.Floor(top), width, height);
        if (_placement == placement) return;
        if (!NativeMethods.SetWindowPos(_handle, nint.Zero, placement.X, placement.Y, width, height,
            NativeMethods.NoActivatePosition | NativeMethods.NoOwnerZOrderPosition | NativeMethods.NoZOrderPosition))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法定位弹幕操作栏。");
        _placement = placement;
    }

    private void OnLikeClick(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(this, EventArgs.Empty);

    private static nint WindowProcedure(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE: accept the click without activating this HWND.
        {
            handled = true;
            return new nint(3);
        }
        return nint.Zero;
    }
}
