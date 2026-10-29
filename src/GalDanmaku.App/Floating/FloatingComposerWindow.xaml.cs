using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GalDanmaku.App.ViewModels;
using GalDanmaku.App.Windows;

namespace GalDanmaku.App.Floating;

public partial class FloatingComposerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Action _showBackground;
    private WindowSnapshot? _snapshot;
    private ComposerOffset? _offset;
    private nint _handle;
    private nint _lastForeground;
    private long _foregroundRevision;
    private ComposerSendEventArgs? _sendAttempt;
    private nint _sendForeground;
    private long _sendRevision;
    private bool _collapsed;
    private bool _closed;
    private bool _shutdown;
    private bool _dragging;
    private ComposerRectangle _workArea;
    private Point? _collapsedDragOrigin;

    public FloatingComposerWindow(MainViewModel viewModel, Action showBackground)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _showBackground = showBackground;
        DataContext = viewModel;
        StylePopup.DataContext = viewModel;
        StylePopup.CustomPopupPlacementCallback = PlacePopup;
        _handle = new WindowInteropHelper(this).EnsureHandle();
        viewModel.WindowSnapshotChanged += OnWindowSnapshotChanged;
        viewModel.FloatingComposerRequested += OnRequested;
        viewModel.CommentSendStarted += OnSendStarted;
        viewModel.CommentSaved += OnSaved;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint handle = new WindowInteropHelper(this).Handle;
        // Tool window only: unlike the renderer, this HWND intentionally accepts focus and input.
        long style = (long)NativeMethods.GetExtendedStyle(handle);
        Marshal.SetLastPInvokeError(0);
        nint previous = NativeMethods.SetExtendedStyle(handle, (nint)((style | NativeMethods.ToolWindowStyle) & ~NativeMethods.AppWindowStyle));
        int error = Marshal.GetLastWin32Error();
        if (previous == nint.Zero && error != 0) throw new Win32Exception(error, "无法配置游戏旁发送条。");
    }

    public void Shutdown()
    {
        _shutdown = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_shutdown)
        {
            e.Cancel = true;
            SetCollapsed(true);
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        StylePopup.IsOpen = false;
        _viewModel.WindowSnapshotChanged -= OnWindowSnapshotChanged;
        _viewModel.FloatingComposerRequested -= OnRequested;
        _viewModel.CommentSendStarted -= OnSendStarted;
        _viewModel.CommentSaved -= OnSaved;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnClosed(e);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (!_closed) Dispatcher.BeginInvoke(() => UpdatePresentation());
    }

    private bool OwnsForeground(nint foreground) => foreground != nint.Zero &&
        (foreground == _handle || (StylePopup.IsOpen && PresentationSource.FromVisual(PopupSurface) is HwndSource popup && foreground == popup.Handle));

    private void ObserveForeground(nint foreground)
    {
        if (_lastForeground == foreground) return;
        _lastForeground = foreground;
        _foregroundRevision++;
    }

    private void OnWindowSnapshotChanged(object? sender, WindowSnapshot snapshot)
    {
        if (_snapshot?.Target != snapshot.Target) _offset = null;
        _snapshot = snapshot;
        ObserveForeground(snapshot.ForegroundHandle);
        UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        if (_closed) return;
        WindowSnapshot? snapshot = _snapshot;
        nint foreground = NativeMethods.GetForegroundWindow();
        bool available = snapshot is { State: TargetState.Visible or TargetState.NotForeground };
        bool targetForeground = snapshot?.Target?.Handle == foreground;
        if (!ComposerPolicy.IsVisible(targetForeground, available, OwnsForeground(foreground)))
        {
            StylePopup.IsOpen = false;
            Hide();
            return;
        }

        try
        {
            if (!_dragging) Position(snapshot!);
            if (!IsVisible)
            {
                Show();
                if (!_dragging) Position(snapshot!);
            }
            if (targetForeground) WindowLayer.PlaceAboveTarget(_handle, snapshot!.Target!.Handle);
            if (targetForeground) FeedbackLabel.Visibility = Visibility.Collapsed;
            PausedLabel.Visibility = targetForeground || FeedbackLabel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Win32Exception exception)
        {
            StylePopup.IsOpen = false;
            Hide();
            _viewModel.ReportComposerStatus($"发送条暂时无法显示：{exception.Message}");
        }
    }

    private void Position(WindowSnapshot snapshot)
    {
        var monitor = new NativeMethods.MonitorInformation { Size = Marshal.SizeOf<NativeMethods.MonitorInformation>() };
        nint monitorHandle = NativeMethods.MonitorFromWindow(snapshot.Target!.Handle, 2);
        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref monitor))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取目标显示器工作区。");
        NativeMethods.NativeRectangle work = monitor.WorkArea;
        // Use our PerMonitorV2 HWND, never the game's HWND (older games often report 96 DPI).
        // Crossing monitors raises OnDpiChanged, which repeats placement with this window's new DPI.
        uint dpi = NativeMethods.GetDpiForWindow(_handle);
        double scale = Math.Max(96, dpi) / 96.0;
        _workArea = new(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
        double availableWidth = (work.Right - work.Left) / scale;
        // Keep the input/send row usable on narrow displays; secondary controls are compact icons.
        NodeLabel.Visibility = availableWidth < 440 ? Visibility.Collapsed : Visibility.Visible;
        double width = (_collapsed ? 96 : 680) * scale;
        double height = (_collapsed ? 42 : 60) * scale;
        ClientBounds bounds = snapshot.Bounds;
        ComposerRectangle placement = ComposerPolicy.Place(new(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            new(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), width, height, 8 * scale, _offset);
        if (!NativeMethods.SetWindowPos(_handle, nint.Zero, (int)placement.X, (int)placement.Y,
            (int)placement.Width, (int)placement.Height,
            NativeMethods.NoActivatePosition | NativeMethods.NoOwnerZOrderPosition | NativeMethods.NoZOrderPosition))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法定位发送条。");
        PopupSurface.Width = Math.Max(1, Math.Min(320, availableWidth - 12));
        PopupSurface.MaxHeight = Math.Max(1, (work.Bottom - work.Top) / scale - 12);
    }

    private CustomPopupPlacement[] PlacePopup(Size popupSize, Size targetSize, Point offset)
    {
        Point origin = StyleButton.PointToScreen(new Point());
        DpiScale dpi = VisualTreeHelper.GetDpi(StyleButton);
        double top = NativeMethods.GetWindowRect(_handle, out NativeMethods.NativeRectangle window) ? window.Top : origin.Y;
        ComposerRectangle placement = ComposerPolicy.Place(new(origin.X, top, 0, 0), _workArea,
            popupSize.Width * dpi.DpiScaleX, popupSize.Height * dpi.DpiScaleY, 0,
            new ComposerOffset(0, -popupSize.Height * dpi.DpiScaleY - 6 * dpi.DpiScaleY));
        return [new CustomPopupPlacement(new Point((placement.X - origin.X) / dpi.DpiScaleX,
            (placement.Y - origin.Y) / dpi.DpiScaleY), PopupPrimaryAxis.None)];
    }

    private void OnRequested(object? sender, EventArgs args)
    {
        _snapshot = _viewModel.CurrentWindowSnapshot;
        if (_snapshot is not { Target: { } target } snapshot) return;
        if (snapshot.State is TargetState.Minimized or TargetState.Hidden)
        {
            _viewModel.ReportComposerStatus("请先还原并显示目标窗口，再使用游戏旁发送条。");
            return;
        }
        if (snapshot.State is not (TargetState.Visible or TargetState.NotForeground))
        {
            _viewModel.ReportComposerStatus("暂时无法读取目标窗口，窗口恢复后可使用发送条。");
            return;
        }
        SetCollapsed(false);
        bool focused = NativeMethods.SetForegroundWindow(target.Handle);
        _viewModel.ReportComposerStatus(focused ? "发送条将在目标窗口旁显示，点击输入后弹幕暂停。" : "切回目标窗口后显示发送条。");
        UpdatePresentation();
    }

    private void OnSendStarted(object? sender, ComposerSendEventArgs attempt)
    {
        nint foreground = NativeMethods.GetForegroundWindow();
        ObserveForeground(foreground);
        _sendAttempt = OwnsForeground(foreground) ? attempt : null;
        _sendForeground = foreground;
        _sendRevision = _foregroundRevision;
        if (_sendAttempt is not null) FeedbackLabel.Visibility = Visibility.Visible;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.LocalStatus) || FeedbackLabel.Visibility != Visibility.Visible) return;
        FeedbackLabel.Text = $"弹幕播放已暂停 · {_viewModel.LocalStatus}";
        FeedbackLabel.ToolTip = _viewModel.LocalStatus;
        PausedLabel.Visibility = Visibility.Collapsed;
    }

    private void OnSaved(object? sender, ComposerSendEventArgs attempt)
    {
        if (!ReferenceEquals(_sendAttempt, attempt)) return;
        _sendAttempt = null;
        nint foreground = NativeMethods.GetForegroundWindow();
        if (_closed || attempt.Target is not { } target || _snapshot?.Target != target ||
            _sendRevision != _foregroundRevision || foreground != _sendForeground || !OwnsForeground(foreground) ||
            !NativeMethods.IsWindow(target.Handle) || NativeMethods.IsIconic(target.Handle) || !NativeMethods.IsWindowVisible(target.Handle) || NativeMethods.IsCloaked(target.Handle) ||
            NativeMethods.GetWindowThreadProcessId(target.Handle, out uint process) != target.ThreadId || process != target.ProcessId) return;
        StylePopup.IsOpen = false;
        if (!NativeMethods.SetForegroundWindow(target.Handle))
        {
            _viewModel.ReportComposerStatus("弹幕已保存；无法切回游戏，请点击游戏窗口继续播放。");
        }
    }

    private void StyleButton_OnClick(object sender, RoutedEventArgs e) => StylePopup.IsOpen = !StylePopup.IsOpen;
    private void BackgroundButton_OnClick(object sender, RoutedEventArgs e)
    {
        StylePopup.IsOpen = false;
        _showBackground();
    }
    private void CollapseButton_OnClick(object sender, RoutedEventArgs e) => SetCollapsed(true);
    private void ExpandButton_OnClick(object sender, RoutedEventArgs e) => SetCollapsed(false);
    private void SetCollapsed(bool collapsed)
    {
        StylePopup.IsOpen = false;
        _collapsed = collapsed;
        ExpandedContent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedHandle.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        UpdatePresentation();
    }

    private void DragHandle_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DragAndRemember();
    }

    private void CollapsedHandle_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _collapsedDragOrigin = e.GetPosition(this);

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_collapsedDragOrigin is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _collapsedDragOrigin = null;
        e.Handled = true;
        CollapsedHandle.ReleaseMouseCapture();
        DragAndRemember();
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _collapsedDragOrigin = null;
        base.OnPreviewMouseLeftButtonUp(e);
    }

    private void DragAndRemember()
    {
        StylePopup.IsOpen = false;
        _dragging = true;
        try { DragMove(); }
        finally { _dragging = false; }
        if (_snapshot is { State: TargetState.Visible or TargetState.NotForeground } snapshot && NativeMethods.GetWindowRect(_handle, out NativeMethods.NativeRectangle rectangle))
            _offset = new ComposerOffset(rectangle.Left - snapshot.Bounds.X, rectangle.Top - snapshot.Bounds.Y);
        UpdatePresentation();
    }
}
