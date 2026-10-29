using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalDanmaku.App.Overlay;
using GalDanmaku.App.Windows;
using GalDanmaku.Core;

namespace GalDanmaku.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly WindowTracker _tracker = new();
    private readonly OverlayWindow _overlay = new();
    private WindowTarget? _selectedWindow;
    private bool _isBound;
    private bool _disposed;
    private string _status = "选择一个窗口，然后绑定。";
    private string _boundTarget = "未绑定窗口";
    private double _fontSize = 28;
    private double _opacityPercent = 90;
    private double _speed = 140;
    private DisplayArea _selectedArea;

    public MainViewModel()
    {
        Areas = [new("顶部 1/4", 0.25), new("上半屏", 0.5), new("全屏", 1)];
        _selectedArea = Areas[1];
        RefreshCommand = new RelayCommand(Refresh);
        BindCommand = new RelayCommand(Bind, () => SelectedWindow is not null && !IsBound);
        UnbindCommand = new RelayCommand(Unbind, () => IsBound);
        _tracker.Changed += OnWindowChanged;
        Refresh();
    }

    public ObservableCollection<WindowTarget> Windows { get; } = [];
    public IReadOnlyList<DisplayArea> Areas { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand BindCommand { get; }
    public IRelayCommand UnbindCommand { get; }

    public WindowTarget? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (SetProperty(ref _selectedWindow, value))
            {
                BindCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBound
    {
        get => _isBound;
        private set
        {
            if (SetProperty(ref _isBound, value))
            {
                BindCommand.NotifyCanExecuteChanged();
                UnbindCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string BoundTarget { get => _boundTarget; private set => SetProperty(ref _boundTarget, value); }

    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (SetProperty(ref _fontSize, value))
            {
                UpdateSettings();
            }
        }
    }

    public double OpacityPercent
    {
        get => _opacityPercent;
        set
        {
            if (SetProperty(ref _opacityPercent, value))
            {
                UpdateSettings();
            }
        }
    }

    public double Speed
    {
        get => _speed;
        set
        {
            if (SetProperty(ref _speed, value))
            {
                UpdateSettings();
            }
        }
    }

    public DisplayArea SelectedArea
    {
        get => _selectedArea;
        set
        {
            if (value is not null && SetProperty(ref _selectedArea, value))
            {
                UpdateSettings();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _tracker.Changed -= OnWindowChanged;
        _tracker.Dispose();
        _overlay.Close();
        _disposed = true;
    }

    private void Refresh()
    {
        try
        {
            nint? selectedHandle = SelectedWindow?.Handle;
            IReadOnlyList<WindowTarget> windows = WindowCatalog.Enumerate();
            Windows.Clear();
            foreach (WindowTarget window in windows)
            {
                Windows.Add(window);
            }

            SelectedWindow = Windows.FirstOrDefault(window => window.Handle == selectedHandle);
            if (!IsBound)
            {
                Status = Windows.Count == 0 ? "没有可选窗口。打开目标应用后刷新列表。" : "选择一个窗口，然后绑定。";
            }
        }
        catch (Win32Exception exception)
        {
            Status = exception.Message;
        }
    }

    private void Bind()
    {
        if (SelectedWindow is not { } target)
        {
            return;
        }

        try
        {
            _tracker.Bind(target);
        }
        catch (InvalidOperationException exception)
        {
            Unbind();
            Status = exception.Message;
        }
    }

    private void Unbind() => _tracker.Unbind();

    private void OnWindowChanged(object? sender, WindowSnapshot snapshot)
    {
        IsBound = snapshot.State is not (TargetState.Unbound or TargetState.Closed);
        BoundTarget = IsBound ? $"{snapshot.Target!.ProcessName} · {snapshot.Target.Title}" : "未绑定窗口";
        Status = snapshot.State switch
        {
            TargetState.Visible => "正在播放模拟弹幕 · 鼠标穿透已开启",
            TargetState.NotForeground => "已绑定。切回目标窗口后自动播放。",
            TargetState.Minimized => "目标已最小化，覆盖层已隐藏。",
            TargetState.Hidden => "目标当前不可见，覆盖层已隐藏。",
            TargetState.Unavailable => "暂时无法读取客户区，等待窗口恢复。",
            TargetState.Closed => "目标窗口已关闭，已自动解除绑定。",
            _ => "已解除绑定。选择窗口可重新开始。",
        };

        if (snapshot.State != TargetState.Visible)
        {
            _overlay.Suspend();
            return;
        }

        try
        {
            _overlay.Follow(snapshot.Bounds);
        }
        catch (Win32Exception exception)
        {
            Unbind();
            Status = $"覆盖层已停止：{exception.Message}";
        }
    }

    private void UpdateSettings() => _overlay.Settings = new DanmakuSettings(FontSize, OpacityPercent / 100, Speed, SelectedArea.Fraction);
}

public sealed record DisplayArea(string Label, double Fraction);
