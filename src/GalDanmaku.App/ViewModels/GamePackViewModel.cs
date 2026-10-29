using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalDanmaku.Core;
using GalDanmaku.Core.GamePacks;

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
    private string _status = "尚未加载游戏包。选择一个游戏包后点击“加载”。";

    public GamePackViewModel(LocalCommentSession session, TextSourceViewModel textSource, StoryLocatorViewModel story,
        Func<string, CancellationToken, Task<LoadedGamePack>>? loader = null, IReadOnlyList<string>? roots = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(textSource);
        ArgumentNullException.ThrowIfNull(story);
        _session = session;
        _textSource = textSource;
        _story = story;
        _loader = loader ?? GamePackLoader.LoadAsync;
        _roots = roots ?? [];
        _story.LocationChanged += OnLocationChanged;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_disposed && !_isRefreshing);
        LoadCommand = new AsyncRelayCommand(LoadSelectedAsync, () => !_disposed && SelectedPack is { IsSelectable: true },
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
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ActiveSummary => _activePack is { } pack ? $"{pack.Name} · v{pack.Version} · {pack.GameId}" : "未加载游戏包";
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
                foreach (GamePackEntry entry in entries)
                    if (seen.Add(entry.Directory)) items.Add(new GamePackItem(entry));
            }

            if (_disposed || generation != _refreshGeneration) return;
            string? selected = SelectedPack?.Directory ?? _activePack?.Directory;
            Packs.Clear();
            foreach (GamePackItem item in items) Packs.Add(item);
            SelectedPack = Packs.FirstOrDefault(item => item.IsSelectable && string.Equals(item.Directory, selected, StringComparison.OrdinalIgnoreCase))
                ?? Packs.FirstOrDefault(item => item.IsSelectable);
            if (!_isLoading && _activePack is null)
                Status = Packs.Count == 0 ? "没有找到游戏包。可将游戏包文件夹放入程序目录的 gamepacks 后刷新。" : $"找到 {Packs.Count} 个游戏包。选择后点击“加载”。";
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Shutdown cancels discovery.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!_disposed && generation == _refreshGeneration) Status = $"扫描游戏包失败：{exception.Message}";
        }
        finally
        {
            if (generation == _refreshGeneration) _isRefreshing = false;
            if (!_disposed) RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    public Task LoadSelectedAsync() => SelectedPack is { IsSelectable: true } item ? LoadPackAsync(item.Directory) : Task.CompletedTask;

    // Atomic switch: the previous pack is fully torn down first, and only the newest request may activate.
    public Task LoadPackAsync(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ObjectDisposedException.ThrowIf(_disposed, this);
        long generation = ++_generation;
        _loadCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loadCancellation = cancellation;
        _inflight.RemoveAll(task => task.IsCompleted);
        Deactivate("正在加载游戏包…");
        SetLoading(true);
        Task task = LoadCoreAsync(directory, generation, cancellation);
        _inflight.Add(task);
        return task;
    }

    private async Task LoadCoreAsync(string directory, long generation, CancellationTokenSource cancellation)
    {
        try
        {
            await _textSource.StopAsync();
            if (IsStale(generation)) return;
            _textSource.SetUnavailable("正在加载游戏包…");
            // The loader's synchronous prefix touches the file system; keep it off the UI thread.
            LoadedGamePack loaded = await Task.Run(() => _loader(directory, cancellation.Token), cancellation.Token);
            if (IsStale(generation)) return;
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
            // Superseded or shutting down: the newer request owns the state now.
        }
        catch (Exception exception)
        {
            // Reported, never swallowed: the app stays in the "no active pack" state set by Deactivate.
            if (!IsStale(generation))
            {
                string reason = $"加载失败：{exception.Message} 当前没有活动游戏包。";
                _textSource.SetUnavailable(reason);
                Status = reason;
            }
        }
        finally
        {
            if (generation == _generation && !_disposed) SetLoading(false);
            if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
            cancellation.Dispose();
        }
    }

    private bool IsStale(long generation) => _disposed || generation != _generation;

    private void Activate(LoadedGamePack loaded)
    {
        GamePack pack = loaded.Pack;
        _activePack = pack;
        _coordinator = new NodeBindingCoordinator(_session, pack.GameId);
        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _story.UseIndex(loaded.Index);
        _textSource.ApplyPack(pack.TextSource, pack.CleanText, pack.Name);
        Status = $"已加载：{pack.Name} · v{pack.Version} · {loaded.Index.Nodes.Count} 个节点";
        OnPropertyChanged(nameof(ActivePack));
        OnPropertyChanged(nameof(HasActivePack));
        OnPropertyChanged(nameof(ActiveSummary));
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    // Synchronous part of the teardown: nothing of the old pack can bind, send or play after this returns.
    private void Deactivate(string reason)
    {
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
