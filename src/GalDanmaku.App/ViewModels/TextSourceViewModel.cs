using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalDanmaku.Core.GamePacks;
using GalDanmaku.Core.Text;

namespace GalDanmaku.App.ViewModels;

public sealed class TextSourceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Func<Action, CancellationToken, Task> _dispatch;
    private readonly Func<bool, TextSourceOptions, ITextSource> _sourceFactory;
    private readonly TextSourceOptions _defaultOptions;
    private CancellationTokenSource? _cancellation;
    private Task _runTask = Task.CompletedTask;
    private bool _disposed;
    private bool _isAvailable = true;
    private bool _rawOnly;
    private IReadOnlyList<string>? _mockScript;
    private Func<string, string>? _cleaner;
    private bool _isRunning;
    private bool _isStopping;
    private string _endpoint;
    private string _initialRetry;
    private string _maximumRetry;
    private string _connectTimeout;
    private string _mergeGap;
    private TextSourceChoice _selectedSource;
    private string _status = "未开始。模拟源无需安装 LunaTranslator。";
    private string _latestText = "";
    private string _latestEvent = "尚无对话事件";
    private long _rawCount;
    private long _newCount;
    private long _updateCount;
    private long _filteredCount;

    public TextSourceViewModel(TextSourceOptions options, Func<Action, CancellationToken, Task> dispatch,
        Func<bool, TextSourceOptions, ITextSource>? sourceFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatch);
        _dispatch = dispatch;
        _defaultOptions = options;
        _sourceFactory = sourceFactory ?? ((mock, settings) => mock ? new MockTextSource(null, _mockScript) : new LunaTextSource(settings));
        _endpoint = options.Endpoint;
        _initialRetry = options.InitialRetryMilliseconds.ToString(CultureInfo.InvariantCulture);
        _maximumRetry = options.MaximumRetryMilliseconds.ToString(CultureInfo.InvariantCulture);
        _connectTimeout = options.ConnectTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
        _mergeGap = options.MergeGapMilliseconds.ToString(CultureInfo.InvariantCulture);
        Sources = [new("模拟文本演示", true), new("LunaTranslator 原文", false)];
        _selectedSource = Sources[0];
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanEdit);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning && !_isStopping && !_disposed);
    }

    public IAsyncRelayCommand StartCommand { get; }
    public event EventHandler<DialogueEvent>? DialogueReceived;
    public event EventHandler? ContextInvalidated;
    public IAsyncRelayCommand StopCommand { get; }
    public IReadOnlyList<TextSourceChoice> Sources { get; }
    public TextSourceChoice SelectedSource { get => _selectedSource; set { if (CanEdit && value is not null && (!_rawOnly || !value.IsMock)) SetProperty(ref _selectedSource, value); } }
    public string Endpoint { get => _endpoint; set { if (CanEdit) SetProperty(ref _endpoint, value); } }
    public string InitialRetry { get => _initialRetry; set { if (CanEdit) SetProperty(ref _initialRetry, value); } }
    public string MaximumRetry { get => _maximumRetry; set { if (CanEdit) SetProperty(ref _maximumRetry, value); } }
    public string ConnectTimeout { get => _connectTimeout; set { if (CanEdit) SetProperty(ref _connectTimeout, value); } }
    public string MergeGap { get => _mergeGap; set { if (CanEdit) SetProperty(ref _mergeGap, value); } }
    public string ConfigurationNotice { get; init; } = "界面修改仅本次运行有效；启动默认值读取程序目录的 text-source.json。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsRunning => _isRunning;
    public bool CanEdit => !_isRunning && !_disposed && _isAvailable;
    public bool IsAvailable => _isAvailable;
    public long RawCount { get => _rawCount; private set => SetProperty(ref _rawCount, value); }
    public long NewCount { get => _newCount; private set => SetProperty(ref _newCount, value); }
    public long UpdateCount { get => _updateCount; private set => SetProperty(ref _updateCount, value); }
    public long FilteredCount { get => _filteredCount; private set => SetProperty(ref _filteredCount, value); }
    public string LatestText { get => _latestText; private set => SetProperty(ref _latestText, value); }
    public string LatestEvent { get => _latestEvent; private set => SetProperty(ref _latestEvent, value); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { await _runTask; return; }
        _disposed = true;
        ContextInvalidated?.Invoke(this, EventArgs.Empty);
        _cancellation?.Cancel();
        UpdateCommands();
        await _runTask;
    }

    // Applies a loaded game pack's declarative text-source settings. Only valid while stopped.
    public void ApplyPack(GamePackTextSource config, Func<string, string> cleaner, string packName, bool lunaOnly = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(cleaner);
        ArgumentNullException.ThrowIfNull(packName);
        if (_disposed || _isRunning) throw new InvalidOperationException("文本源仍在运行，无法应用游戏包配置。");
        TextSourceOptions options = config.Options;
        _endpoint = options.Endpoint;
        _initialRetry = options.InitialRetryMilliseconds.ToString(CultureInfo.InvariantCulture);
        _maximumRetry = options.MaximumRetryMilliseconds.ToString(CultureInfo.InvariantCulture);
        _connectTimeout = options.ConnectTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
        _mergeGap = options.MergeGapMilliseconds.ToString(CultureInfo.InvariantCulture);
        _selectedSource = !lunaOnly && config.Kind == GamePackTextSourceKind.Mock ? Sources[0] : Sources[1];
        _rawOnly = lunaOnly;
        _mockScript = config.MockScript;
        _cleaner = cleaner;
        _isAvailable = true;
        ClearCounters();
        foreach (string name in new[] { nameof(Endpoint), nameof(InitialRetry), nameof(MaximumRetry), nameof(ConnectTimeout), nameof(MergeGap), nameof(SelectedSource) })
            OnPropertyChanged(name);
        Status = $"已应用游戏包「{packName}」的文本源配置，可开始接收。";
        UpdateCommands();
    }

    // The caller stops the old source before installing either a pack or the unindexed Luna defaults.
    public void SetUnavailable(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        _isAvailable = false;
        _mockScript = null;
        _cleaner = null;
        Status = reason;
        UpdateCommands();
    }

    public void UseUnindexedLuna(string reason)
    {
        if (_disposed || _isRunning) throw new InvalidOperationException("文本源仍在运行，无法恢复默认原文配置。");
        ApplyPack(new GamePackTextSource(GamePackTextSourceKind.Luna, _defaultOptions, []), text => text, "默认原文接收", lunaOnly: true);
        _cleaner = null;
        _mockScript = null;
        Status = $"{reason} 可接收 Luna 原文；没有剧情索引，弹幕不可用。";
    }

    private void ClearCounters()
    {
        RawCount = NewCount = UpdateCount = FilteredCount = 0;
        LatestText = "";
        LatestEvent = "尚无对话事件";
    }

    private Task StartAsync()
    {
        if (!CanEdit) return Task.CompletedTask;
        if (_rawOnly && SelectedSource.IsMock)
        {
            Status = "已绑定窗口时只能接收 Luna 原文，不能启动模拟文本源。";
            return Task.CompletedTask;
        }
        TextSourceOptions options;
        try
        {
            options = new TextSourceOptions
            {
                Endpoint = Endpoint.Trim(),
                InitialRetryMilliseconds = ParseMilliseconds(InitialRetry),
                MaximumRetryMilliseconds = ParseMilliseconds(MaximumRetry),
                ConnectTimeoutMilliseconds = ParseMilliseconds(ConnectTimeout),
                MergeGapMilliseconds = ParseMilliseconds(MergeGap)
            };
            options.Validate();
        }
        catch (ArgumentException exception) { Status = $"配置无效：{exception.Message}"; return Task.CompletedTask; }

        ClearCounters();
        Status = "正在启动文本源…";
        ContextInvalidated?.Invoke(this, EventArgs.Empty);
        _cancellation = new CancellationTokenSource();
        _isRunning = true;
        _isStopping = false;
        UpdateCommands();
        _runTask = RunAsync(options, SelectedSource.IsMock, _cleaner, _cancellation);
        return _runTask;
    }

    private async Task RunAsync(TextSourceOptions options, bool mock, Func<string, string>? cleaner, CancellationTokenSource owner)
    {
        var processor = new DialogueProcessor(TimeSpan.FromMilliseconds(options.MergeGapMilliseconds));
        // Set on disconnect: the story locator is reset then, so the first repeated frame must be re-published.
        bool reacquire = false;
        try
        {
            ITextSource source = _sourceFactory(mock, options);
            await source.RunAsync((item, token) => new ValueTask(_dispatch(() =>
            {
                // A canceled dispatcher work item may already be queued; never apply it to a stopped/restarted session.
                if (_disposed || owner.IsCancellationRequested || !ReferenceEquals(owner, _cancellation)) return;
                if (item is TextConnectionChanged connection)
                {
                    if (connection.State == TextConnectionState.Retrying)
                    {
                        processor.OnDisconnected();
                        reacquire = true;
                        ContextInvalidated?.Invoke(this, EventArgs.Empty);
                    }
                    Status = connection.Detail;
                }
                else if (item is TextFrame frame)
                {
                    RawCount++;
                    TextProcessResult result = processor.Process(cleaner is null ? frame.Text : cleaner(frame.Text), frame.ReceivedAt);
                    DialogueEvent? published = result.Event;
                    if (published is null && reacquire && result.FilterReason == TextFilterReason.Duplicate)
                        published = processor.Reacquire(frame.ReceivedAt);
                    if (result.FilterReason is not (TextFilterReason.Empty or TextFilterReason.Invalid)) reacquire = false;
                    if (published is not { } dialogue) { FilteredCount++; return; }
                    if (dialogue.Kind == DialogueEventKind.NewDialogue) NewCount++; else UpdateCount++;
                    LatestText = dialogue.Text;
                    DialogueReceived?.Invoke(this, dialogue);
                    LatestEvent = $"{(dialogue.Kind == DialogueEventKind.NewDialogue ? "新对话 · NewDialogue" : "文本更新 · TextUpdated")} · 会话内 #{dialogue.DialogueId} · 事件 {dialogue.Sequence} · {dialogue.ReceivedAt.ToLocalTime():HH:mm:ss.fff}";
                }
            }, token)), owner.Token);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
            // Start/Stop and window shutdown share this awaited cancellation path.
        }
        catch (Exception exception)
        {
            if (!_disposed) Status = $"文本源失败（{exception.GetType().Name}），请检查配置后重新开始。";
        }
        finally
        {
            if (!_disposed) ContextInvalidated?.Invoke(this, EventArgs.Empty);
            if (!_disposed && owner.IsCancellationRequested) Status = "已停止接收。再次开始会重置本次计数。";
            _isRunning = false;
            _isStopping = false;
            if (ReferenceEquals(owner, _cancellation)) _cancellation = null;
            owner.Dispose();
            if (!_disposed) UpdateCommands();
        }
    }

    // Safe to call when nothing is running; completes once the run task has ended.
    public async Task StopAsync()
    {
        if (_cancellation is null) { await _runTask; return; }
        _isStopping = true;
        ContextInvalidated?.Invoke(this, EventArgs.Empty);
        Status = "正在停止…";
        _cancellation.Cancel();
        UpdateCommands();
        await _runTask;
    }

    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanEdit));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private static int ParseMilliseconds(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result)
        ? result : throw new ArgumentException("时间设置须为非负整数（毫秒）。");
}

public sealed record TextSourceChoice(string Label, bool IsMock);
