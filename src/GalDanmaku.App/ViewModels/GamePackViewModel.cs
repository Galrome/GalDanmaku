using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalDanmaku.Core;
using GalDanmaku.Core.GamePacks;
using GalDanmaku.Core.Diagnostics;

namespace GalDanmaku.App.ViewModels;

public sealed record GamePackItem(GamePackEntry Entry)
{
    public string Directory => Entry.Directory;
    public bool IsSelectable => Entry.Error is null;
    public string Title => IsSelectable ? $"{Entry.DisplayName} · v{Entry.Version}" : $"{Entry.DisplayName}（不可用）";
    public string Detail => IsSelectable ? $"{Entry.GameId} · {Entry.Directory}" : $"错误：{Entry.Error}";
}

// Owns the single active game pack and everything derived from it: story index, text source
// configuration and the node binding coordinator. Contains no WPF types so it can be unit tested.
// All members must be used on the owning (UI) thread.
public sealed class GamePackViewModel : ObservableObject, IAsyncDisposable
{
    private readonly LocalCommentSession _session;
    private readonly TextSourceViewModel _textSource;
    private readonly StoryLocatorViewModel _story;
    private readonly Func<string, CancellationToken, Task<LoadedGamePack>> _loader;
    private readonly IReadOnlyList<string> _roots;
    private readonly string _mappingPath;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _inflight = [];
    private CancellationTokenSource? _loadCancellation;
    private NodeBindingCoordinator? _coordinator;
    private GamePack? _activePack;
    private GamePackItem? _selectedPack;
    private long _generation;
    private long _refreshGeneration;
    private bool _isLoading;
    private bool _isRefreshing;
    private bool _disposed;
    private bool _windowBound;
    private string? _requiredGameId;
    private string _identitySummary = "";
    private string _status = "尚未加载游戏包。选择一个游戏包后点击“加载”。";

    public GamePackViewModel(LocalCommentSession session, TextSourceViewModel textSource, StoryLocatorViewModel story,
        Func<string, CancellationToken, Task<LoadedGamePack>>? loader = null, IReadOnlyList<string>? roots = null,
        string? mappingPath = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(textSource);
        ArgumentNullException.ThrowIfNull(story);
        _session = session;
        _textSource = textSource;
        _story = story;
        _loader = loader ?? GamePackLoader.LoadAsync;
        _roots = roots ?? [];
        _mappingPath = mappingPath ?? Path.Combine(AppContext.BaseDirectory, "game-mappings", "local-debug.json");
        _story.LocationChanged += OnLocationChanged;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_disposed && !_isRefreshing);
        LoadCommand = new AsyncRelayCommand(LoadSelectedAsync, () => !_disposed && SelectedPack is { IsSelectable: true } item && CanLoadItem(item),
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        // Nothing is active yet: locating, text and comments stay unavailable until a pack loads.
        _textSource.SetUnavailable("尚未加载游戏包，文本源不可用。");
        _story.ClearIndex("尚未加载游戏包。");
    }

    public ObservableCollection<GamePackItem> Packs { get; } = [];
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand LoadCommand { get; }
    public NodeBindingCoordinator? Coordinator => _coordinator;
    public GamePack? ActivePack => _activePack;
    public bool HasActivePack => _activePack is not null;
    public bool IsLoading => _isLoading;
    public string GameIdentity => _identitySummary;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ActiveSummary => _activePack is { } pack ? $"{pack.Name} · v{pack.Version} · {pack.GameId}"
        : string.IsNullOrEmpty(_identitySummary) ? "未加载游戏包" : $"{_identitySummary} · 未加载剧情包";
    // Raised when the active pack, its coordinator or the coordinator state changed.
    public event EventHandler? ActiveChanged;

    public GamePackItem? SelectedPack
    {
        get => _selectedPack;
        set
        {
            if (SetProperty(ref _selectedPack, value)) LoadCommand.NotifyCanExecuteChanged();
        }
    }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        long generation = ++_refreshGeneration;
        CancellationToken token = _lifetime.Token;
        _isRefreshing = true;
        RefreshCommand.NotifyCanExecuteChanged();
        try
        {
            var items = new List<GamePackItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in _roots)
            {
                if (_disposed) return;
                IReadOnlyList<GamePackEntry> entries = await GamePackCatalog.DiscoverAsync(root, token);
                RuntimeLog.Write(LogLevel.Information, "GamePack", "GamePackCatalogScanned", new { root, count = entries.Count, invalidCount = entries.Count(entry => entry.Error is not null) });
                foreach (GamePackEntry entry in entries)
                    if (seen.Add(entry.Directory)) items.Add(new GamePackItem(entry));
            }

            if (_disposed || generation != _refreshGeneration) return;
            string? selected = SelectedPack?.Directory ?? _activePack?.Directory;
            Packs.Clear();
            foreach (GamePackItem item in items) Packs.Add(item);
            SelectedPack = Packs.FirstOrDefault(item => item.IsSelectable && string.Equals(item.Directory, selected, StringComparison.OrdinalIgnoreCase))
                ?? Packs.FirstOrDefault(item => item.IsSelectable);
            if (!_isLoading && _activePack is null && !_windowBound)
                Status = Packs.Count == 0 ? "没有找到游戏包。可将游戏包文件夹放入程序目录的 gamepacks 后刷新。" : $"找到 {Packs.Count} 个游戏包。选择后点击“加载”。";
        }
        catch (OperationCanceledException) when (_disposed)
        {
            RuntimeLog.Write(LogLevel.Debug, "GamePack", "GamePackScanCancelled", new { reason = "ApplicationClosing" });
            // Shutdown cancels discovery.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            RuntimeLog.Write(LogLevel.Error, "GamePack", "GamePackScanFailed", exception: exception);
            if (!_disposed && generation == _refreshGeneration && !_windowBound) Status = $"扫描游戏包失败：{exception.Message}";
        }
        finally
        {
            if (generation == _refreshGeneration) _isRefreshing = false;
            if (!_disposed) RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    public Task LoadSelectedAsync() => SelectedPack is { IsSelectable: true } item ? LoadPackAsync(item.Directory) : Task.CompletedTask;

    private bool CanLoadItem(GamePackItem item) => !_windowBound
        || _requiredGameId is not null && string.Equals(item.Entry.GameId, _requiredGameId, StringComparison.Ordinal);

    // These entry points synchronously revoke the previous index before any asynchronous work starts.
    public Task BindWindowAsync(string? executablePath, string? identityError = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _windowBound = true;
        RuntimeLog.SetGameId(null);
        RuntimeLog.Write(LogLevel.Information, "GamePack", "GameRecognitionStarted", new { executablePath, identityError });
        _requiredGameId = null;
        _identitySummary = "正在识别游戏";
        (long generation, CancellationTokenSource cancellation) = BeginChange("正在识别游戏并匹配剧情包…");
        return Track(BindWindowCoreAsync(executablePath, identityError, generation, cancellation));
    }

    public Task ClearWindowBindingAsync()
    {
        if (_disposed) return Task.CompletedTask;
        _windowBound = false;
        RuntimeLog.Write(LogLevel.Information, "GamePack", "GameContextCleared", new { reason = "WindowUnboundOrClosed" });
        RuntimeLog.SetGameId(null);
        _requiredGameId = null;
        _identitySummary = "";
        (long generation, CancellationTokenSource cancellation) = BeginChange("已解除窗口绑定，未加载剧情包。");
        return Track(ClearWindowCoreAsync(generation, cancellation));
    }

    private (long, CancellationTokenSource) BeginChange(string reason)
    {
        long generation = ++_generation;
        _loadCancellation?.Cancel();
        if (_isLoading) RuntimeLog.Write(LogLevel.Debug, "GamePack", "GamePackRequestSuperseded", new { generation, reason });
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loadCancellation = cancellation;
        Deactivate(reason);
        SetLoading(true);
        LoadCommand.NotifyCanExecuteChanged();
        return (generation, cancellation);
    }

    private Task Track(Task task)
    {
        _inflight.RemoveAll(pending => pending.IsCompleted);
        _inflight.Add(task);
        return task;
    }

    private async Task BindWindowCoreAsync(string? executablePath, string? identityError, long generation, CancellationTokenSource cancellation)
    {
        using var operation = RuntimeLog.BeginScope(new LogContext(OperationId: Guid.NewGuid().ToString("N")));
        try
        {
            await _textSource.StopAsync();
            if (IsStale(generation)) return;
            LocalGameMatch match;
            if (string.IsNullOrWhiteSpace(executablePath))
                match = new(LocalGameMatchStatus.Unknown, null, identityError is null
                    ? "无法读取进程路径，未识别游戏。" : $"无法读取进程路径，未识别游戏：{identityError}");
            else
            {
                LocalGameMappings mappings = await Task.Run(() => LocalGameMappings.LoadAsync(_mappingPath, cancellation.Token), cancellation.Token);
                if (IsStale(generation)) return;
                match = mappings.Match(executablePath);
            }
            if (match.Game is not { } game)
            {
                RuntimeLog.Write(LogLevel.Warning, "GamePack", "GameRecognitionUncertain", new { status = match.Status.ToString(), match.Reason });
                _identitySummary = match.Status == LocalGameMatchStatus.Ambiguous ? "游戏识别存在歧义" : "未识别游戏";
                UseUnindexedLuna(match.Reason);
                return;
            }

            _identitySummary = $"已识别：{game.DisplayName} · {game.Id}";
            _requiredGameId = game.RequiredGamePackId;
            RuntimeLog.SetGameId(game.Id);
            using var recognizedGame = RuntimeLog.BeginScope(new LogContext(GameId: game.Id));
            RuntimeLog.Write(LogLevel.Information, "GamePack", "GameRecognized", new { game.Id, game.DisplayName, requiredGamePackId = _requiredGameId });
            LoadCommand.NotifyCanExecuteChanged();
            var candidates = new List<GamePackEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in _roots)
            {
                IReadOnlyList<GamePackEntry> entries = await GamePackCatalog.DiscoverAsync(root, cancellation.Token);
                if (IsStale(generation)) return;
                RuntimeLog.Write(LogLevel.Debug, "GamePack", "GamePackCandidatesScanned", new { root, count = entries.Count,
                    invalid = entries.Where(entry => entry.Error is not null).Select(entry => new { entry.Directory, entry.Error }).ToArray() });
                candidates.AddRange(entries.Where(entry => seen.Add(entry.Directory)
                    && entry.Error is null && string.Equals(entry.GameId, _requiredGameId, StringComparison.Ordinal)));
            }
            if (candidates.Count != 1)
            {
                RuntimeLog.Write(LogLevel.Warning, "GamePack", candidates.Count == 0 ? "GamePackMissing" : "GamePackAmbiguous",
                    new { requiredGameId = _requiredGameId, candidateCount = candidates.Count, reason = candidates.Count == 0 ? "NoValidPackForGameId" : "MultiplePacksForGameId" });
                UseUnindexedLuna(candidates.Count == 0 ? $"{_identitySummary}；无可用剧情包。"
                    : $"{_identitySummary}；同一 GameId 存在多个剧情包，未自动选择。");
                return;
            }

            await LoadCoreAsync(candidates[0].Directory, generation, cancellation, _requiredGameId, ownsCancellation: false);
        }
        catch (OperationCanceledException) when (IsStale(generation))
        {
            RuntimeLog.Write(LogLevel.Debug, "GamePack", "GameRecognitionCancelled", new { generation, reason = _disposed ? "ApplicationClosing" : "Superseded" });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            RuntimeLog.Write(LogLevel.Error, "GamePack", "GameRecognitionFailed", new { generation, stale = IsStale(generation) }, exception);
            if (!IsStale(generation))
            {
                if (_requiredGameId is null) _identitySummary = "未识别游戏";
                UseUnindexedLuna($"{(_requiredGameId is null ? "" : _identitySummary + "；")}自动匹配失败：{exception.Message}");
            }
        }
        finally { FinishChange(generation, cancellation); }
    }

    private async Task ClearWindowCoreAsync(long generation, CancellationTokenSource cancellation)
    {
        try
        {
            await _textSource.StopAsync();
            if (!IsStale(generation)) UseUnindexedLuna("已解除窗口绑定，未加载剧情包。");
        }
        finally { FinishChange(generation, cancellation); }
    }

    private void UseUnindexedLuna(string reason)
    {
        _textSource.UseUnindexedLuna(reason);
        Status = reason;
        OnPropertyChanged(nameof(GameIdentity));
        OnPropertyChanged(nameof(ActiveSummary));
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FinishChange(long generation, CancellationTokenSource cancellation)
    {
        if (generation == _generation && !_disposed) SetLoading(false);
        if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
        cancellation.Dispose();
    }

    // Atomic switch: the previous pack is fully torn down first, and only the newest request may activate.
    public Task LoadPackAsync(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowBound && (_requiredGameId is null || !Packs.Any(item => item.IsSelectable && CanLoadItem(item)
            && string.Equals(item.Directory, directory, StringComparison.OrdinalIgnoreCase))))
        {
            RuntimeLog.Write(LogLevel.Warning, "GamePack", "GamePackLoadRejected", new { directory, requiredGameId = _requiredGameId, reason = "BoundGameIdentityMismatch" });
            Status = "已绑定窗口时只能加载已识别游戏对应的剧情包；解除绑定后可使用演示包。";
            return Task.CompletedTask;
        }
        (long generation, CancellationTokenSource cancellation) = BeginChange("正在加载游戏包…");
        return Track(LoadCoreAsync(directory, generation, cancellation, _windowBound ? _requiredGameId : null));
    }

    private async Task LoadCoreAsync(string directory, long generation, CancellationTokenSource cancellation,
        string? requiredGameId = null, bool ownsCancellation = true)
    {
        using var operation = RuntimeLog.BeginScope(new LogContext(OperationId: Guid.NewGuid().ToString("N")));
        RuntimeLog.Write(LogLevel.Information, "GamePack", "GamePackLoadStarted", new { directory, requiredGameId, generation });
        try
        {
            await _textSource.StopAsync();
            if (IsStale(generation)) return;
            _textSource.SetUnavailable("正在加载游戏包…");
            // The loader's synchronous prefix touches the file system; keep it off the UI thread.
            LoadedGamePack loaded = await Task.Run(() => _loader(directory, cancellation.Token), cancellation.Token);
            if (IsStale(generation)) return;
            if (requiredGameId is not null && (!string.Equals(loaded.Pack.GameId, requiredGameId, StringComparison.Ordinal)
                || !string.Equals(loaded.Index.GameId, requiredGameId, StringComparison.Ordinal)))
                throw new InvalidDataException("剧情包 GameId 与已识别游戏不一致，已拒绝加载。");
            if (requiredGameId is not null && (loaded.Pack.TextSource.Kind == GamePackTextSourceKind.Mock
                || string.Equals(loaded.Pack.Engine, "Synthetic", StringComparison.OrdinalIgnoreCase)
                || loaded.Pack.GameId.Contains("synthetic", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("真实窗口不能关联模拟游戏包或模拟文本源。");
            // A cancelled node load of the previous coordinator may still hold the session busy.
            while (_session.IsBusy)
            {
                await Task.Delay(10, cancellation.Token);
                if (IsStale(generation)) return;
            }

            if (IsStale(generation)) return;
            Activate(loaded);
        }
        catch (OperationCanceledException) when (IsStale(generation))
        {
            RuntimeLog.Write(LogLevel.Information, "GamePack", "GamePackLoadCancelled", new { generation, reason = _disposed ? "ApplicationClosing" : "Superseded" });
            // Superseded or shutting down: the newer request owns the state now.
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(LogLevel.Error, "GamePack", "GamePackLoadFailed", new { directory, generation, stale = IsStale(generation) }, exception);
            // Reported, never swallowed: the app stays in the "no active pack" state set by Deactivate.
            if (!IsStale(generation))
            {
                string reason = $"{(_windowBound ? _identitySummary + "；" : "")}加载失败：{exception.Message} 当前没有活动游戏包。";
                if (_windowBound) UseUnindexedLuna(reason);
                else { _textSource.SetUnavailable(reason); Status = reason; }
            }
        }
        finally
        {
            if (ownsCancellation) FinishChange(generation, cancellation);
        }
    }

    private bool IsStale(long generation) => _disposed || generation != _generation;

    private void Activate(LoadedGamePack loaded)
    {
        GamePack pack = loaded.Pack;
        if (!_windowBound) RuntimeLog.SetGameId(pack.GameId);
        RuntimeLog.Write(LogLevel.Information, "GamePack", "GamePackLoaded", new { pack.GameId, pack.Name, pack.Version, pack.Directory, nodeCount = loaded.Index.Nodes.Count });
        _activePack = pack;
        _coordinator = new NodeBindingCoordinator(_session, pack.GameId);
        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _story.UseIndex(loaded.Index);
        _textSource.ApplyPack(pack.TextSource, pack.CleanText, pack.Name, lunaOnly: _windowBound);
        Status = $"已加载：{pack.Name} · v{pack.Version} · {loaded.Index.Nodes.Count} 个节点";
        OnPropertyChanged(nameof(ActivePack));
        OnPropertyChanged(nameof(HasActivePack));
        OnPropertyChanged(nameof(ActiveSummary));
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    // Synchronous part of the teardown: nothing of the old pack can bind, send or play after this returns.
    private void Deactivate(string reason)
    {
        RuntimeLog.Write(LogLevel.Debug, "GamePack", "GamePackDeactivated", new { previousGameId = _activePack?.GameId, reason });
        if (_coordinator is { } coordinator)
        {
            coordinator.StateChanged -= OnCoordinatorStateChanged;
            coordinator.Reset();
            coordinator.Dispose();
            _coordinator = null;
        }

        _session.Unload();
        _activePack = null;
        _story.ClearIndex("尚未加载游戏包。");
        _textSource.SetUnavailable(reason);
        Status = reason;
        OnPropertyChanged(nameof(ActivePack));
        OnPropertyChanged(nameof(HasActivePack));
        OnPropertyChanged(nameof(ActiveSummary));
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetLoading(bool value)
    {
        if (_isLoading == value) return;
        _isLoading = value;
        OnPropertyChanged(nameof(IsLoading));
    }

    private void OnLocationChanged(object? sender, StoryLocationChangedEventArgs args)
    {
        if (_coordinator is not { } coordinator) return;
        // A hand-imported index has no game pack, so it can never bind comments.
        if (!args.DrivesBinding) coordinator.Reset();
        else coordinator.OnLocationChanged(args.Location, args.ConfirmationVersion);
    }

    private void OnCoordinatorStateChanged(object? sender, EventArgs args) => ActiveChanged?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _story.LocationChanged -= OnLocationChanged;
        _loadCancellation?.Cancel();
        _lifetime.Cancel();
        if (_coordinator is { } coordinator)
        {
            coordinator.StateChanged -= OnCoordinatorStateChanged;
            coordinator.Dispose();
            _coordinator = null;
        }

        Task[] pending = _inflight.ToArray();
        foreach (Task task in pending)
        {
            await task;
        }

        _lifetime.Dispose();
    }
}
