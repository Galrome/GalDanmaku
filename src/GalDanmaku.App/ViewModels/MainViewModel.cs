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
using GalDanmaku.Core.GamePacks;
using GalDanmaku.Core.Text;
using GalDanmaku.Storage;
using Microsoft.Data.Sqlite;

namespace GalDanmaku.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly WindowTracker _tracker = new();
    private readonly OverlayWindow _overlay;
    private readonly LocalCommentSession _session;
    private readonly DispatcherTimer _nodeClock;
    private bool _localBusy;
    private string _draftText = "";
    private string _localStatus = "未加载游戏包，弹幕不可用。";
    private CommentColor _selectedColor;
    private WindowTarget? _selectedWindow;
    private bool _isBound;
    private bool _disposed;
    private string _status = "选择一个窗口，然后绑定。";
    private string _boundTarget = "未绑定窗口";
    private double _opacityPercent = 90;
    private double _speed = 140;
    private DisplayArea _selectedArea;
    private WindowSnapshot? _lastSnapshot;

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
        Story = new StoryLocatorViewModel(TextSource, Path.Combine(AppContext.BaseDirectory, "story-demo.json"));
        string databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalDanmaku", "danmaku.db");
        var repository = new SqliteCommentRepository(databasePath);
        _session = new LocalCommentSession(repository);
        _overlay = new OverlayWindow(_session.Playback);
        InitializeLikes(repository);
        var roots = new List<string> { Path.Combine(AppContext.BaseDirectory, "gamepacks") };
        string userPacks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalDanmaku", "gamepacks");
        if (Directory.Exists(userPacks)) roots.Add(userPacks);
        Pack = new GamePackViewModel(_session, TextSource, Story, roots: roots);
        InitializeDemo();
        Pack.ActiveChanged += OnPackActiveChanged;
        TextSource.PropertyChanged += OnTextSourceChanged;
        Colors = [new("白色", "#FFFFFF"), new("金色", "#FFD166"), new("天蓝", "#80D8FF")];
        _selectedColor = Colors[0];
        InitializeCommand = new AsyncRelayCommand(InitializeAsync, () => IsLocalIdle);
        ReplayCommand = new RelayCommand(Replay, () => CanReplay);
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
        _nodeClock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _nodeClock.Tick += OnNodeClockTick;
        _nodeClock.Start();
        Areas = [new("顶部 1/4", 0.25), new("上半屏", 0.5), new("全屏", 1)];
        _selectedArea = Areas[1];
        UpdateSettings();
        RefreshCommand = new RelayCommand(Refresh);
        BindCommand = new AsyncRelayCommand(BindAsync, () => SelectedWindow is not null && !IsBound && !_disposed);
        UnbindCommand = new RelayCommand(Unbind, () => IsBound);
        ShowFloatingComposerCommand = new RelayCommand(RequestFloatingComposer);
        _tracker.Changed += OnWindowChanged;
        _tracker.LayerUpdated += OnWindowLayerUpdated;
        Refresh();
    }

    public ObservableCollection<WindowTarget> Windows { get; } = [];
    public GamePackViewModel Pack { get; }
    public TextSourceViewModel TextSource { get; }
    public StoryLocatorViewModel Story { get; }
    public IReadOnlyList<DisplayArea> Areas { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand BindCommand { get; }
    public IRelayCommand UnbindCommand { get; }
    public IAsyncRelayCommand InitializeCommand { get; }
    public IRelayCommand ReplayCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }
    public IReadOnlyList<CommentColor> Colors { get; }
    public bool IsLocalIdle => !_localBusy && !_disposed;
    public bool CanSend => IsLocalIdle && Pack.Coordinator is { CanSend: true } && !string.IsNullOrWhiteSpace(DraftText);
    public bool CanReplay => IsLocalIdle && Pack.Coordinator is { State: NodeBindingState.Active, BoundNodeId: not null } or { State: NodeBindingState.Failed };
    public string ActivePackText => Pack.ActiveSummary;
    public string BoundNodeText => Pack.Coordinator?.BoundNodeId is { } nodeId ? $"当前节点：{nodeId}" : "当前节点：未确认，弹幕已暂停";
    public string CoordinatorText => Pack.Coordinator is not { } coordinator ? "未加载游戏包，弹幕不可用。" : coordinator.State switch
    {
        NodeBindingState.Loading => "正在加载当前节点的弹幕…",
        NodeBindingState.Active when IsTargetVisible => "已确认当前节点，弹幕播放中。",
        NodeBindingState.Active when !IsBound => "已确认当前节点。尚未绑定目标窗口，绑定后才会显示弹幕。",
        NodeBindingState.Active when _lastSnapshot?.State == TargetState.NotForeground => "已确认当前节点。弹幕已暂停并保留当前画面，切回目标窗口继续播放。",
        NodeBindingState.Active => "已确认当前节点。目标窗口回到前台后播放弹幕。",
        NodeBindingState.Failed => $"节点弹幕加载失败：{coordinator.LastError} 通常是本地数据库被占用或不可写；处理后点击“重播 / 重试当前节点”。",
        _ when !TextSource.IsRunning => "等待剧情定位确认节点。文本源未运行，请在“实时文本接收”中点击“开始接收”。",
        _ => "等待剧情定位确认节点。",
    };
    public int SavedCount => _session.Playback.SavedCount;
    // The coordinator marks playback as running once a node loads, but the clock only advances while the
    // overlay renders, i.e. while the target is in the foreground. Report what actually happens.
    private bool IsTargetVisible => _lastSnapshot?.State == TargetState.Visible;
    public string NodeTime => $"节点时间 {_session.Playback.OffsetMilliseconds / 1000.0:0.0} 秒 · {(IsTargetVisible && _session.Playback.IsPlaying ? "播放中" : "已暂停")}";
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
        // Stop polling the target first so nothing repositions or unbinds while the rest is torn down.
        _tracker.Changed -= OnWindowChanged;
        _tracker.LayerUpdated -= OnWindowLayerUpdated;
        _tracker.Dispose();
        _overlay.Suspend();
        await DisposeLikesAsync();
        Pack.ActiveChanged -= OnPackActiveChanged;
        Story.PropertyChanged -= OnDemoStoryChanged;
        TextSource.PropertyChanged -= OnTextSourceChanged;
        // Cancels and awaits any in-flight pack load and disposes the coordinator before the session goes away.
        await Pack.DisposeAsync();
        await Story.DisposeAsync();
        await TextSource.DisposeAsync();
        // A save in flight is canceled by the session: the repository commits atomically or rolls back.
        // Wait for it so the process never exits while the database thread is still writing.
        _session.Dispose();
        if (SendCommand.ExecutionTask is { } sending) await Task.WhenAny(sending);
        _overlay.Close();
    }

    private async Task InitializeAsync()
    {
        await Pack.RefreshAsync();
        if (!_disposed) UpdateLocalState();
    }

    private void Replay()
    {
        if (!CanReplay) return;
        if (Pack.Coordinator is { State: NodeBindingState.Failed } failed)
        {
            failed.Retry();
            return;
        }

        _session.Playback.Replay();
        _overlay.ResetVisuals();
        LocalStatus = $"正在重播节点 {Pack.Coordinator!.BoundNodeId}。";
    }

    // Coordinator/pack changes: the overlay is cleared whenever the bound node changes or disappears,
    // and only plays while a node is actually active (and the target window is in the foreground).
    private void OnPackActiveChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        UpdateBoundTarget();
        StartDemoCommand.NotifyCanExecuteChanged();
        _overlay.ResetVisuals();
        _overlay.PlaybackEnabled = Pack.Coordinator is { State: NodeBindingState.Active };
        LocalStatus = Pack.Coordinator switch
        {
            null => "未加载游戏包，弹幕不可用。",
            { State: NodeBindingState.Active } when SavedCount == 0 => "这个节点还没有弹幕。输入内容后发送，切回目标窗口即可看到。",
            { State: NodeBindingState.Active } => $"已加载 {SavedCount} 条弹幕，将按保存的节点时间重播。",
            { State: NodeBindingState.Loading } => "正在加载当前节点的弹幕…",
            { State: NodeBindingState.Failed } coordinator => $"节点弹幕加载失败：{coordinator.LastError} 点击“重播 / 重试当前节点”可重试。",
            _ => "未确认剧情节点，弹幕已暂停。",
        };
        UpdateLocalState();
        // The window line must not keep claiming playback after the node was lost (or keep denying it once one is active).
        if (_lastSnapshot is { State: not (TargetState.Unbound or TargetState.Closed) } snapshot) Status = DescribeWindow(snapshot);
    }

    private void OnTextSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_disposed && args.PropertyName == nameof(TextSourceViewModel.IsRunning)) OnPropertyChanged(nameof(CoordinatorText));
        if (!_disposed && args.PropertyName == nameof(TextSourceViewModel.SelectedSource)) StartDemoCommand.NotifyCanExecuteChanged();
        if (!_disposed && args.PropertyName == nameof(TextSourceViewModel.CanEdit)) StartTextReceptionCommand.NotifyCanExecuteChanged();
    }

    private void UpdateLocalState()
    {
        OnPropertyChanged(nameof(ActivePackText));
        OnPropertyChanged(nameof(BoundNodeText));
        OnPropertyChanged(nameof(CoordinatorText));
        OnPropertyChanged(nameof(SavedCount));
        OnPropertyChanged(nameof(NodeTime));
        OnPropertyChanged(nameof(ComposerNodeText));
        SendCommand.NotifyCanExecuteChanged();
        ReplayCommand.NotifyCanExecuteChanged();
    }

    private async Task SendAsync()
    {
        if (!CanSend)
        {
            return;
        }

        NodeBindingCoordinator coordinator = Pack.Coordinator!;
        string draft = DraftText;
        var attempt = new ComposerSendEventArgs(_lastSnapshot?.Target);
        CommentSendStarted?.Invoke(this, attempt);
        bool savedSuccessfully = false;
        SetLocalBusy(true);
        LocalStatus = "正在保存弹幕…";
        try
        {
            StoredDanmaku saved = await coordinator.SendAsync(draft, SelectedColor.Value, DanmakuSettings.DefaultFontSize);
            if (_disposed) return;
            if (DraftText == draft) DraftText = "";
            LocalStatus = $"已保存到节点 {saved.NodeId} · {saved.OffsetMilliseconds / 1000.0:0.0} 秒。切回目标窗口播放。";
            savedSuccessfully = true;
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // The repository rolls back before commit; committed entries reload on the next launch.
        }
        catch (Exception exception) when (IsLocalDataError(exception))
        {
            if (!_disposed) LocalStatus = $"发送失败：{DescribeLocalError(exception)} 输入内容已保留，可直接重试。";
        }
        finally
        {
            if (!_disposed) SetLocalBusy(false);
        }
        // UI focus restoration happens after persistence handling and cannot turn a committed save into a data error.
        if (savedSuccessfully && !_disposed) CommentSaved?.Invoke(this, attempt);
    }

    private void SetLocalBusy(bool value)
    {
        _localBusy = value;
        OnPropertyChanged(nameof(IsLocalIdle));
        InitializeCommand.NotifyCanExecuteChanged();
        UpdateLocalState();
    }

    private void OnNodeClockTick(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(NodeTime));
        Story.RefreshClock();
    }

    // SQLITE_BUSY (5) / SQLITE_LOCKED (6) / SQLITE_CANTOPEN (14): the raw English driver text is not actionable.
    private static string DescribeLocalError(Exception exception) => exception switch
    {
        SqliteException { SqliteErrorCode: 5 or 6 } => "本地数据库正被其他程序占用，请稍后重试。",
        SqliteException { SqliteErrorCode: 14 } => "无法打开本地数据库文件，请检查磁盘空间与目录权限。",
        SqliteException sqlite => $"本地数据库读写失败（{sqlite.Message}）。",
        IOException or UnauthorizedAccessException => $"无法访问本地数据库文件（{exception.Message}）。",
        _ => exception.Message,
    };

    private static bool IsLocalDataError(Exception exception) => exception is
        SqliteException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException;

    private void Refresh()
    {
        try
        {
            WindowTarget? selected = SelectedWindow;
            IReadOnlyList<WindowTarget> windows = WindowCatalog.Enumerate();
            Windows.Clear();
            foreach (WindowTarget window in windows)
            {
                Windows.Add(window);
            }

            SelectedWindow = Windows.FirstOrDefault(window => selected is not null && window.Handle == selected.Handle
                && window.ProcessId == selected.ProcessId && window.ThreadId == selected.ThreadId);
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

    private async Task BindAsync()
    {
        if (SelectedWindow is not { } target)
        {
            return;
        }

        try
        {
            target = WindowCatalog.ReadProcessIdentity(target);
            Task matching = Pack.BindWindowAsync(target.ExecutablePath, target.ProcessIdentityError);
            _tracker.Bind(target);
            await matching;
        }
        catch (InvalidOperationException exception)
        {
            Unbind();
            Refresh(); // The closed window must not stay selectable.
            Status = exception.Message;
        }
    }

    private void Unbind()
    {
        _ = Pack.ClearWindowBindingAsync();
        _tracker.Unbind();
    }

    private void UpdateBoundTarget()
    {
        BoundTarget = IsBound && _lastSnapshot?.Target is { } target
            ? $"{target.ProcessName} · {target.Title} · {Pack.GameIdentity}" : "未绑定窗口";
    }

    private void OnWindowChanged(object? sender, WindowSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        WindowSnapshotChanged?.Invoke(this, snapshot);
        IsBound = snapshot.State is not (TargetState.Unbound or TargetState.Closed);
        UpdateBoundTarget();
        // A window that closed on its own must not stay selectable in the list.
        if (snapshot.State == TargetState.Closed) Refresh();
        Status = DescribeWindow(snapshot);
        OnPropertyChanged(nameof(CoordinatorText));
        OnPropertyChanged(nameof(NodeTime));

        // A closed target revokes the entire game context, including pending pack loads.
        if (snapshot.State == TargetState.Closed) _ = Pack.ClearWindowBindingAsync();

        if (snapshot.State is not (TargetState.Visible or TargetState.NotForeground))
        {
            _overlay.Suspend();
            return;
        }

        try
        {
            _overlay.Follow(snapshot.Bounds, snapshot.Target!, snapshot.State == TargetState.Visible);
        }
        catch (Win32Exception exception)
        {
            Unbind();
            Status = $"覆盖层已停止：{exception.Message}";
        }
    }

    private void OnWindowLayerUpdated(object? sender, WindowSnapshot snapshot)
    {
        if (_disposed || snapshot.Target is not { } target) return;
        try
        {
            _overlay.SyncZOrder(target);
        }
        catch (Win32Exception exception)
        {
            Unbind();
            Status = $"覆盖层已停止：{exception.Message}";
        }
    }

    private string DescribeWindow(WindowSnapshot snapshot)
    {
        string text = snapshot.State switch
        {
            TargetState.Visible => Pack.Coordinator switch
            {
                null => "已绑定，目标在前台。未加载游戏包，暂无弹幕。",
                { State: NodeBindingState.Active } => "正在播放当前节点的本地弹幕 · 鼠标穿透已开启",
                { State: NodeBindingState.Loading } => "已绑定 · 正在加载当前节点的弹幕…",
                { State: NodeBindingState.Failed } => "已绑定 · 节点弹幕加载失败，可点击“重播 / 重试当前节点”。",
                _ => "已绑定，目标在前台。尚未确认剧情节点，弹幕已暂停。",
            },
            TargetState.NotForeground => "已绑定。弹幕已暂停并保留当前画面，切回目标窗口继续播放。",
            TargetState.Minimized => "目标已最小化，覆盖层已隐藏。",
            TargetState.Hidden => "目标当前不可见，覆盖层已隐藏。",
            TargetState.Unavailable => "暂时无法读取客户区，等待窗口恢复。",
            TargetState.Closed => "目标窗口已关闭，已自动解除绑定。",
            _ => "已解除绑定。选择窗口可重新开始。",
        };
        if (snapshot.State is not (TargetState.Closed or TargetState.Unbound) && snapshot.Target is { } target
            && Pack.ActivePack is { } pack && pack.ProcessNames.Count > 0 && !pack.MatchesProcess(target.ProcessName))
            text += $" 提示：该进程与游戏包「{pack.Name}」声明的进程不符。";
        return text;
    }

    private void UpdateSettings() => _overlay.Settings = new DanmakuSettings(opacity: OpacityPercent / 100, speed: Speed,
        areaFraction: SelectedArea.Fraction, aggregationEnabled: IsAggregationEnabled);
}

public sealed record DisplayArea(string Label, double Fraction);
public sealed record CommentColor(string Label, string Value);
