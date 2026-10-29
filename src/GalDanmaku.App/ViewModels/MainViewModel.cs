using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalDanmaku.App.Overlay;
using GalDanmaku.App.Windows;
using GalDanmaku.Core;
using GalDanmaku.Core.Text;
using GalDanmaku.Storage;
using Microsoft.Data.Sqlite;

namespace GalDanmaku.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly WindowTracker _tracker = new();
    private readonly OverlayWindow _overlay;
    private readonly LocalCommentSession _session;
    private readonly DispatcherTimer _nodeClock;
    private readonly IAsyncRelayCommand<MockNode> _switchNodeCommand;
    private bool _localBusy;
    private string _draftText = "";
    private string _localStatus = "正在加载本地弹幕…";
    private MockNode _selectedNode;
    private CommentColor _selectedColor;
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
        TextSourceOptions textOptions = new();
        string configNotice = "界面修改仅本次运行有效；启动默认值读取程序目录的 text-source.json。";
        try { textOptions = TextSourceOptions.Load(Path.Combine(AppContext.BaseDirectory, "text-source.json")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { configNotice = "text-source.json 读取或校验失败，已使用内置默认值；原文件未改动。可在此调整设置后开始。"; }
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        TextSource = new TextSourceViewModel(textOptions, (action, token) => dispatcher.InvokeAsync(action, DispatcherPriority.Background, token).Task)
        { ConfigurationNotice = configNotice };
        string databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalDanmaku", "danmaku.db");
        _session = new LocalCommentSession(new SqliteCommentRepository(databasePath));
        _overlay = new OverlayWindow(_session.Playback);
        Nodes = [new("node-a", "模拟节点 A"), new("node-b", "模拟节点 B")];
        _selectedNode = Nodes[0];
        Colors = [new("白色", "#FFFFFF"), new("金色", "#FFD166"), new("天蓝", "#80D8FF")];
        _selectedColor = Colors[0];
        InitializeCommand = new AsyncRelayCommand(() => LoadNodeAsync(SelectedNode), () => IsLocalIdle);
        ReplayCommand = new AsyncRelayCommand(() => LoadNodeAsync(SelectedNode), () => IsLocalIdle);
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
        _switchNodeCommand = new AsyncRelayCommand<MockNode>(node => LoadNodeAsync(node!), _ => IsLocalIdle);
        _nodeClock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _nodeClock.Tick += OnNodeClockTick;
        _nodeClock.Start();
        Areas = [new("顶部 1/4", 0.25), new("上半屏", 0.5), new("全屏", 1)];
        _selectedArea = Areas[1];
        RefreshCommand = new RelayCommand(Refresh);
        BindCommand = new RelayCommand(Bind, () => SelectedWindow is not null && !IsBound);
        UnbindCommand = new RelayCommand(Unbind, () => IsBound);
        _tracker.Changed += OnWindowChanged;
        Refresh();
    }

    public ObservableCollection<WindowTarget> Windows { get; } = [];
    public TextSourceViewModel TextSource { get; }
    public IReadOnlyList<DisplayArea> Areas { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand BindCommand { get; }
    public IRelayCommand UnbindCommand { get; }
    public IAsyncRelayCommand InitializeCommand { get; }
    public IAsyncRelayCommand ReplayCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }
    public IReadOnlyList<MockNode> Nodes { get; }
    public IReadOnlyList<CommentColor> Colors { get; }
    public bool IsLocalIdle => !_localBusy && !_disposed;
    public bool CanSend => IsLocalIdle && _session.IsLoaded && !string.IsNullOrWhiteSpace(DraftText);
    public int SavedCount => _session.Playback.SavedCount;
    public string NodeTime => $"节点时间 {_session.Playback.OffsetMilliseconds / 1000.0:0.0} 秒 · {(_session.Playback.IsPlaying ? "播放中" : "已暂停")}";
    public string LocalStatus { get => _localStatus; private set => SetProperty(ref _localStatus, value); }
    public string DraftText
    {
        get => _draftText;
        set
        {
            if (SetProperty(ref _draftText, value))
            {
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public MockNode SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (value is not null && value != _selectedNode && IsLocalIdle)
            {
                _switchNodeCommand.Execute(value);
            }
        }
    }

    public CommentColor SelectedColor
    {
        get => _selectedColor;
        set { if (value is not null) SetProperty(ref _selectedColor, value); }
    }

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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nodeClock.Stop();
        _nodeClock.Tick -= OnNodeClockTick;
        await TextSource.DisposeAsync();
        _session.Dispose();
        _tracker.Changed -= OnWindowChanged;
        _tracker.Dispose();
        _overlay.Close();
    }

    private async Task LoadNodeAsync(MockNode node)
    {
        if (!IsLocalIdle)
        {
            return;
        }

        SetLocalBusy(true);
        _overlay.PlaybackEnabled = false;
        LocalStatus = $"正在加载{node.Label}…";
        try
        {
            await _session.LoadAsync("mock-game", node.Id);
            if (_disposed) return;
            SetProperty(ref _selectedNode, node, nameof(SelectedNode));
            _overlay.ResetVisuals();
            LocalStatus = SavedCount == 0
                ? "这个节点还没有弹幕。输入内容后发送，切回目标窗口即可看到。"
                : $"已加载 {SavedCount} 条弹幕，将按保存的节点时间重播。";
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Closing cancels pending I/O; the window no longer needs a status update.
        }
        catch (Exception exception) when (IsLocalDataError(exception))
        {
            if (!_disposed) LocalStatus = $"加载失败：{exception.Message} 点击“重播 / 重新加载”重试。";
        }
        finally
        {
            if (!_disposed)
            {
                OnPropertyChanged(nameof(SelectedNode));
                SetLocalBusy(false);
                _overlay.PlaybackEnabled = _session.IsLoaded;
            }
        }
    }

    private async Task SendAsync()
    {
        if (!CanSend)
        {
            return;
        }

        string draft = DraftText;
        SetLocalBusy(true);
        LocalStatus = "正在保存弹幕…";
        try
        {
            StoredDanmaku saved = await _session.SendAsync(draft, SelectedColor.Value, FontSize);
            if (_disposed) return;
            if (DraftText == draft) DraftText = "";
            LocalStatus = $"已保存到{SelectedNode.Label} · {saved.OffsetMilliseconds / 1000.0:0.0} 秒。切回目标窗口播放。";
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // The repository rolls back before commit; committed entries reload on the next launch.
        }
        catch (Exception exception) when (IsLocalDataError(exception))
        {
            if (!_disposed) LocalStatus = $"发送失败：{exception.Message} 输入内容已保留。";
        }
        finally
        {
            if (!_disposed) SetLocalBusy(false);
        }
    }

    private void SetLocalBusy(bool value)
    {
        _localBusy = value;
        OnPropertyChanged(nameof(IsLocalIdle));
        OnPropertyChanged(nameof(SavedCount));
        OnPropertyChanged(nameof(NodeTime));
        InitializeCommand.NotifyCanExecuteChanged();
        ReplayCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        _switchNodeCommand.NotifyCanExecuteChanged();
    }

    private void OnNodeClockTick(object? sender, EventArgs args) => OnPropertyChanged(nameof(NodeTime));

    private static bool IsLocalDataError(Exception exception) => exception is
        SqliteException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException;

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
            TargetState.Visible => "正在播放当前节点的本地弹幕 · 鼠标穿透已开启",
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
public sealed record MockNode(string Id, string Label);
public sealed record CommentColor(string Label, string Value);
