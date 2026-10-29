using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GalDanmaku.App.Windows;
using GalDanmaku.Core;

namespace GalDanmaku.App.Overlay;

public sealed class OverlayWindow : Window
{
    private readonly OverlayRenderer _renderer = new();
    private HwndSource? _source;
    private nint _handle;

    public OverlayWindow()
    {
        Title = "GalDanmaku Overlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        Topmost = true;
        Width = 1;
        Height = 1;
        Content = _renderer;
    }

    public DanmakuSettings Settings
    {
        get => _renderer.Settings;
        set => _renderer.Settings = value;
    }

    public void Follow(ClientBounds bounds)
    {
        _handle = new WindowInteropHelper(this).EnsureHandle();
        Position(bounds);
        if (!IsVisible)
        {
            Show();
            // WPF can reapply its cached location during Show; native pixels remain authoritative.
            Position(bounds);
        }

        _renderer.Start();
    }

    public void Suspend()
    {
        _renderer.Stop();
        Hide();
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
            throw new Win32Exception(error, "无法配置鼠标穿透覆盖层。");
        }

        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowProcedure);
    }

    protected override void OnClosed(EventArgs e)
    {
        _renderer.Dispose();
        _source?.RemoveHook(WindowProcedure);
        _source = null;
        base.OnClosed(e);
    }

    private void Position(ClientBounds bounds)
    {
        if (!NativeMethods.SetWindowPos(_handle, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height,
            NativeMethods.NoActivatePosition | NativeMethods.NoOwnerZOrderPosition))
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
