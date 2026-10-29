using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalDanmaku.Core.Story;
using GalDanmaku.Core.Text;

namespace GalDanmaku.App.ViewModels;

// Raised after every locator state change (including resets). DrivesBinding is false when the index did not
// come from a game pack, in which case the location must never bind or load comments.
public sealed class StoryLocationChangedEventArgs(StoryLocation location, long confirmationVersion, bool drivesBinding) : EventArgs
{
    public StoryLocation Location { get; } = location;
    public long ConfirmationVersion { get; } = confirmationVersion;
    public bool DrivesBinding { get; } = drivesBinding;
}

public sealed class StoryLocatorViewModel : ObservableObject, IAsyncDisposable
{
    private readonly TextSourceViewModel _source;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, CancellationToken, Task<StoryIndex>> _loader;
    private readonly CancellationTokenSource _lifetime = new();
    private StoryIndex? _index;
    private StoryLocator? _locator;
    private string _indexPath;
    private string _indexStatus = "尚未加载剧情索引。";
    private string _latestText = "";
    private string _manualText = "";
    private StoryDemoScenario _selectedScenario;
    private bool _isLiveMode;
    private bool _loading;
    private bool _disposed;
    private bool _drivesBinding;
    private bool _packActive;
    private CancellationTokenSource? _importCancellation;
    private long _indexGeneration;
    private Guid _sessionId = Guid.NewGuid();
    private long _dialogueId;
    private long _sequence;
    private int _demoPosition;

    public StoryLocatorViewModel(TextSourceViewModel source, string defaultIndexPath, TimeProvider? timeProvider = null,
        Func<string, CancellationToken, Task<StoryIndex>>? loader = null)
    {
        _source = source;
        _indexPath = defaultIndexPath;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _loader = loader ?? StoryIndex.LoadAsync;
        Scenarios = [new("route-a", "路线 A"), new("route-b", "路线 B"), new("load-repeat", "读档至重复台词"), new("typing", "逐字输出")];
        _selectedScenario = Scenarios[0];
        LoadCommand = new AsyncRelayCommand(LoadAsync, () => CanImport);
        ResetCommand = new RelayCommand(Reset, () => !_disposed);
        NextDemoCommand = new RelayCommand(NextDemo, () => CanDemo);
        NewDialogueCommand = new RelayCommand(() => ApplyManual(false), () => CanInput);
        UpdateDialogueCommand = new RelayCommand(() => ApplyManual(true), () => CanInput && _dialogueId > 0);
        source.DialogueReceived += OnDialogueReceived;
        source.ContextInvalidated += OnContextInvalidated;
    }

    public event EventHandler<StoryLocationChangedEventArgs>? LocationChanged;
    public IAsyncRelayCommand LoadCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand NextDemoCommand { get; }
    public IRelayCommand NewDialogueCommand { get; }
    public IRelayCommand UpdateDialogueCommand { get; }
    public IReadOnlyList<StoryDemoScenario> Scenarios { get; }
    public bool CanLoad => !_loading && !_disposed;
    // Manual JSON import is a locate-only debugging aid and is unavailable while a game pack provides the index.
    public bool CanImport => CanLoad && !_packActive;
    public bool CanInput => CanLoad && !IsLiveMode && _locator is not null;
    public bool HasDemoFrames => DemoFrames().Count > 0;
    public bool CanDemo => CanInput && DemoFrames().Count > _demoPosition;
    public string IndexPath { get => _indexPath; set { if (CanLoad) SetProperty(ref _indexPath, value); } }
    public string IndexStatus { get => _indexStatus; private set => SetProperty(ref _indexStatus, value); }
    public string LatestText { get => _latestText; private set => SetProperty(ref _latestText, value); }
    public string ManualText { get => _manualText; set => SetProperty(ref _manualText, value); }
    public bool DrivesBinding => _drivesBinding;
    public StoryLocation CurrentLocation => _locator?.Current ?? NoLocation;
    public long ConfirmationVersion => _locator?.ConfirmationVersion ?? 0;
    public StoryLocationStatus LocationStatus => _locator?.Current.Status ?? StoryLocationStatus.Unknown;
    public string? CurrentNodeId => _locator?.Current.Node?.Id;
    public long? OffsetMilliseconds => _locator?.OffsetMilliseconds;
    public int HistoryCount => _locator?.HistoryCount ?? 0;
    public string StatusText => LocationStatus switch
    {
        StoryLocationStatus.Confirmed => "Confirmed · 已确认",
        StoryLocationStatus.Ambiguous => "Ambiguous · 存在多个候选",
        _ => "Unknown · 未确认"
    };
    public string Reason => _locator?.Current.Reason ?? "加载索引后，可逐句演示或使用实时文本。";
    public string NodeText => CurrentNodeId ?? "—";
    public string CandidateText => _locator is null || _locator.Current.Candidates.Count == 0 ? "无"
        : string.Join("、", _locator.Current.Candidates.Take(12).Select(node => node.Id))
            + (_locator.Current.Candidates.Count > 12 ? $" 等 {_locator.Current.Candidates.Count} 个" : "");
    public string OffsetText => OffsetMilliseconds is { } offset ? $"offset_ms: {offset} · 确认后经过 {offset / 1000.0:0.0} 秒"
        : "offset_ms: — · 未确认时不计时";
    public string DemoProgress => DemoFrames().Count == 0 ? "当前索引无内置演示，可在下方手动输入原文。"
        : $"演示 {_demoPosition} / {DemoFrames().Count} · 切换场景或重置可重新开始";

    public bool IsLiveMode
    {
        get => _isLiveMode;
        set { if (CanLoad && SetProperty(ref _isLiveMode, value)) Reset(); }
    }

    public StoryDemoScenario SelectedScenario
    {
        get => _selectedScenario;
        set { if (value is not null && CanInput && SetProperty(ref _selectedScenario, value)) Reset(); }
    }

    // Installs the index of the active game pack. Replaces any previous index and context.
    public void UseIndex(StoryIndex index, string? status = null, bool liveMode = true)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (_disposed) return;
        _indexGeneration++;
        _index = index;
        _locator = new StoryLocator(index, _timeProvider);
        _drivesBinding = true;
        _packActive = true;
        _loading = false;
        _importCancellation?.Cancel();
        if (_isLiveMode != liveMode) { _isLiveMode = liveMode; OnPropertyChanged(nameof(IsLiveMode)); }
        IndexStatus = status ?? $"{index.GameId} · {index.Nodes.Count} 个固定节点 · 来自游戏包";
        Reset();
    }

    // Drops the index entirely so no stale game remains locatable (no game pack active / pack failed).
    public void ClearIndex(string status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (_disposed) return;
        _indexGeneration++;
        _index = null;
        _locator = null;
        _drivesBinding = false;
        _packActive = false;
        _loading = false;
        _importCancellation?.Cancel();
        IndexStatus = status;
        Reset();
    }

    public void RefreshClock() => OnPropertyChanged(nameof(OffsetText));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _source.DialogueReceived -= OnDialogueReceived;
        _source.ContextInvalidated -= OnContextInvalidated;
        _lifetime.Cancel();
        _locator?.Reset();
        NotifyState();
        if (LoadCommand.ExecutionTask is { } loading) await loading;
        _lifetime.Dispose();
    }

    private async Task LoadAsync()
    {
        if (!CanImport) return;
        string path = IndexPath.Trim();
        _loading = true;
        long generation = ++_indexGeneration;
        using var import = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _importCancellation = import;
        // A failed replacement must not leave a previously confirmed game/node active.
        // A manually imported index has no game pack, so it never drives comment binding.
        _index = null;
        _locator = null;
        _drivesBinding = false;
        Reset();
        IndexStatus = "正在读取剧情索引…";
        try
        {
            StoryIndex index = await _loader(path, import.Token);
            // A game pack (or a newer import) may have installed its own index meanwhile; never overwrite it.
            if (_disposed || generation != _indexGeneration) return;
            _index = index;
            _locator = new StoryLocator(index, _timeProvider);
            IndexStatus = $"{index.GameId} · {index.Nodes.Count} 个固定节点 · 已加载（仅定位调试，不绑定弹幕）";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || generation != _indexGeneration)
        {
            // Window shutdown or a game pack taking over cancels the import; a late result must not be applied.
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!_disposed && generation == _indexGeneration) IndexStatus = $"加载失败：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_importCancellation, import)) _importCancellation = null;
            if (generation == _indexGeneration)
            {
                _loading = false;
                if (!_disposed) NotifyState();
            }
        }
    }

    public void Reset()
    {
        _locator?.Reset();
        _sessionId = Guid.NewGuid();
        _dialogueId = _sequence = 0;
        _demoPosition = 0;
        LatestText = "";
        NotifyState();
    }

    private void OnDialogueReceived(object? sender, DialogueEvent dialogue)
    {
        if (_disposed || !IsLiveMode || _locator is null) return;
        LatestText = dialogue.Text;
        _locator.Process(dialogue);
        NotifyState();
    }

    private void OnContextInvalidated(object? sender, EventArgs args)
    {
        if (IsLiveMode && !_disposed) Reset();
    }

    private void ApplyManual(bool update)
    {
        if (!CanInput || (update && _dialogueId == 0)) return;
        ApplyText(ManualText, update);
    }

    private void NextDemo()
    {
        if (!CanDemo) return;
        var frame = DemoFrames()[_demoPosition++];
        ApplyText(frame.Text, frame.Update);
    }

    private void ApplyText(string text, bool update)
    {
        if (!update) _dialogueId++;
        LatestText = text;
        _locator!.Process(new(_sessionId, _dialogueId, ++_sequence, _timeProvider.GetUtcNow(),
            update ? DialogueEventKind.TextUpdated : DialogueEventKind.NewDialogue, text));
        NotifyState();
    }

    private IReadOnlyList<(string Text, bool Update)> DemoFrames()
    {
        if (_index?.GameId != "phase4-synthetic") return [];
        if (SelectedScenario.Id == "typing")
        {
            if (!_index.Nodes.TryGetValue("demo:long", out StoryNode? node) || node.Text.Length < 3) return [];
            return [(node.Text[..1], false), (node.Text[..3], true), (node.Text, true)];
        }
        string route = SelectedScenario.Id == "route-b" ? "b" : "a";
        string[] ids = SelectedScenario.Id == "load-repeat"
            ? ["demo:a-repeat", "demo:a-repeat2", "demo:a-end", "demo:converge"]
            : [$"demo:{route}-intro", $"demo:{route}-repeat", $"demo:{route}-repeat2", $"demo:{route}-end", "demo:converge"];
        if (ids.Any(id => !_index.Nodes.ContainsKey(id))) return [];
        return ids.Select(id => (_index.Nodes[id].Text, false)).ToArray();
    }

    private void NotifyState()
    {
        foreach (string property in new[] { nameof(CanLoad), nameof(CanImport), nameof(CanInput), nameof(HasDemoFrames), nameof(CanDemo), nameof(LocationStatus),
            nameof(CurrentNodeId), nameof(OffsetMilliseconds), nameof(HistoryCount), nameof(StatusText), nameof(Reason),
            nameof(NodeText), nameof(CandidateText), nameof(OffsetText), nameof(DemoProgress) }) OnPropertyChanged(property);
        LoadCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        NextDemoCommand.NotifyCanExecuteChanged();
        NewDialogueCommand.NotifyCanExecuteChanged();
        UpdateDialogueCommand.NotifyCanExecuteChanged();
        LocationChanged?.Invoke(this, new StoryLocationChangedEventArgs(CurrentLocation, ConfirmationVersion, _drivesBinding));
    }

    private static readonly StoryLocation NoLocation = new(StoryLocationStatus.Unknown, null, [], "尚未加载剧情索引。");
}

public sealed record StoryDemoScenario(string Id, string Label);
