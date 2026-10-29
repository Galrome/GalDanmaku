using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GalDanmaku.App.Floating;
using GalDanmaku.App.ViewModels;
using GalDanmaku.App.Views;
using GalDanmaku.App.Windows;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace GalDanmaku.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly FloatingComposerWindow _composer;
    private bool _closing;
    private bool _readyToClose;
    private bool _initialPlacementApplied;
    private readonly Dictionary<string, UserControl> _pages = [];

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _composer = new FloatingComposerWindow(_viewModel, ShowBackground);
        var overview = new OverviewPage();
        overview.ConnectionRequested += (_, _) => ConnectionNavigation.IsChecked = true;
        _pages.Add("Overview", overview);
        _pages.Add("Connection", new ConnectionPage());
        _pages.Add("Appearance", new AppearancePage());
        _pages.Add("Diagnostics", new DiagnosticsPage());
        PageHost.Content = overview;
        Loaded += (_, _) => _viewModel.InitializeCommand.Execute(null);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_initialPlacementApplied) return;
        _initialPlacementApplied = true;

        // CenterScreen and WPF's initial DPI negotiation have finished. Query the real HWND,
        // rather than combining the primary monitor's DIP work area with another monitor's DPI.
        nint handle = new WindowInteropHelper(this).Handle;
        const uint monitorDefaultToNearest = 2;
        var monitor = new NativeMethods.MonitorInformation { Size = Marshal.SizeOf<NativeMethods.MonitorInformation>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(handle, monitorDefaultToNearest), ref monitor))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取控制台所在显示器工作区。");
        if (!NativeMethods.GetWindowRect(handle, out NativeMethods.NativeRectangle window))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取控制台窗口位置。");

        uint dpi = NativeMethods.GetDpiForWindow(handle);
        if (dpi == 0) throw new InvalidOperationException("无法读取控制台窗口 DPI。");
        double scale = dpi / 96.0;
        NativeMethods.NativeRectangle work = monitor.WorkArea;
        int currentWidth = window.Right - window.Left;
        int currentHeight = window.Bottom - window.Top;
        int width = Math.Min(currentWidth, work.Right - work.Left);
        int height = Math.Min(currentHeight, work.Bottom - work.Top);
        double availableWidth = (work.Right - work.Left) / scale;
        double availableHeight = (work.Bottom - work.Top) / scale;
        if (MinWidth > availableWidth) MinWidth = availableWidth;
        if (MinHeight > availableHeight) MinHeight = availableHeight;

        // Keep virtual-screen positions in physical pixels, including negative monitor origins.
        int left = Math.Clamp(window.Left, work.Left, work.Right - width);
        int top = Math.Clamp(window.Top, work.Top, work.Bottom - height);
        bool move = left != window.Left || top != window.Top;
        bool resize = width != currentWidth || height != currentHeight;
        if (!move && !resize) return;

        // One native change is enough: WPF synchronizes its geometry through WM_SIZE/WM_MOVE.
        // Avoid a redundant no-op SetWindowPos after the first frame has been rendered.
        const uint noSizePosition = 0x0001;
        const uint noMovePosition = 0x0002;
        const uint noZOrderPosition = 0x0004;
        uint flags = noZOrderPosition | NativeMethods.NoActivatePosition | NativeMethods.NoOwnerZOrderPosition;
        if (!move) flags |= noMovePosition;
        if (!resize) flags |= noSizePosition;
        if (!NativeMethods.SetWindowPos(handle, nint.Zero, left, top, width, height, flags))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法调整控制台窗口位置。");
    }

    private void OnNavigationChecked(object sender, RoutedEventArgs args)
    {
        if (sender is RadioButton { Tag: string key } && _pages.TryGetValue(key, out UserControl? page))
            PageHost.Content = page;
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (NavigationColumn is null || OverviewLabel is null) return;
        bool compact = ActualWidth < 860;
        NavigationColumn.Width = new GridLength(compact ? 64 : 176);
        Visibility labelVisibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (TextBlock label in new[] { OverviewLabel, ConnectionLabel, AppearanceLabel, DiagnosticsLabel, StorageCaption })
            label.Visibility = labelVisibility;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_readyToClose) { base.OnClosing(e); return; }
        e.Cancel = true;
        base.OnClosing(e);
        if (_closing) return;
        _closing = true;
        _composer.Shutdown();
        IsEnabled = false;
        // The original canceled Closing event must unwind even when disposal completes synchronously.
        await Task.Yield();
        string? failure = null;
        try
        {
            await _viewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            // A failing teardown must not leave a disabled window that can never close.
            failure = $"{exception.GetType().Name}：{exception.Message}";
        }

        if (failure is not null)
            System.Windows.MessageBox.Show($"关闭时清理资源出错：{failure}\n已提交的弹幕不受影响。", "GalDanmaku",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        _readyToClose = true;
        Close();
    }

    private void ShowBackground()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }
}
