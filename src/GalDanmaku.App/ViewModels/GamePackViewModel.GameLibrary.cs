using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using GalDanmaku.Core;
using GalDanmaku.Core.GameIdentity;
using GalDanmaku.Core.GamePacks;
using GalDanmaku.Integrations.LunaTranslator;

namespace GalDanmaku.App.ViewModels;

public sealed partial class GamePackViewModel
{
    private LunaGameLibraryProvider? _lunaLibrary;
    private string _lunaLibraryDirectory = "";
    private string _libraryStatus = "未配置 Luna 游戏库目录；已有本机映射仍然可用。";
    private string _identityDetails = "尚无游戏身份。";
    private string? _boundExecutablePath;
    private GameLibraryEntry? _selectedLibraryCandidate;
    private bool _confirmingAssociation;
    private long _libraryGeneration;
    private readonly SemaphoreSlim _libraryConfigurationGate = new(1, 1);

    public string LunaLibraryDirectory { get => _lunaLibraryDirectory; set => SetProperty(ref _lunaLibraryDirectory, value); }
    public string LibraryStatus { get => _libraryStatus; private set => SetProperty(ref _libraryStatus, value); }
    public string IdentityDetails { get => _identityDetails; private set => SetProperty(ref _identityDetails, value); }
    public ObservableCollection<GameLibraryEntry> IdentityCandidates { get; } = [];
    public GameLibraryEntry? SelectedLibraryCandidate
    {
        get => _selectedLibraryCandidate;
        set
        {
            if (SetProperty(ref _selectedLibraryCandidate, value))
            {
                if (value is not null) ShowIdentityDetails(value.VndbId, value.BangumiId, "Luna 候选 · 待用户确认", null);
                ConfirmAssociationCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public IAsyncRelayCommand RefreshLibraryCommand { get; private set; } = null!;
    public IAsyncRelayCommand ConfirmAssociationCommand { get; private set; } = null!;

    private string LibraryLocationFile => Path.Combine(_identitySettingsDirectory, "luna-library-location.txt");

    private void InitializeGameLibrary()
    {
        RefreshLibraryCommand = new AsyncRelayCommand(() => Track(RefreshLibraryAsync()), () => !_disposed);
        ConfirmAssociationCommand = new AsyncRelayCommand(() => Track(ConfirmAssociationAsync()), CanConfirmAssociation);
        // Explicit local preference only: never scan the machine for other applications' libraries.
        if (_developerSession) return;
        try
        {
            if (File.Exists(LibraryLocationFile))
            {
                var info = new FileInfo(LibraryLocationFile);
                if (info.Length > 32768) throw new InvalidDataException("游戏库目录配置过大。");
                LunaLibraryDirectory = File.ReadAllText(LibraryLocationFile).Trim();
                _lunaLibrary = new LunaGameLibraryProvider(LunaLibraryDirectory);
                LibraryStatus = "已恢复 Luna 游戏库目录，将在绑定窗口或刷新时读取。";
            }
        }
        catch (Exception error) when (IsIdentityFileError(error))
        {
            LibraryStatus = "无法读取游戏库目录设置；可重新选择目录，已有本机映射仍然可用。";
        }
    }

    public Task ConfigureLunaLibraryAsync(string path) => _disposed ? Task.CompletedTask : Track(ConfigureLunaLibraryCoreAsync(path));

    private async Task ConfigureLunaLibraryCoreAsync(string path)
    {
        if (_disposed || _developerSession) return;
        bool entered = false;
        string? pending = null;
        try
        {
            await _libraryConfigurationGate.WaitAsync(_lifetime.Token);
            entered = true;
            string directory = string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path.Trim());
            Directory.CreateDirectory(_identitySettingsDirectory);
            pending = LibraryLocationFile + ".tmp";
            await File.WriteAllTextAsync(pending, directory, _lifetime.Token);
            if (_disposed) return;
            File.Move(pending, LibraryLocationFile, overwrite: true);
            LunaLibraryDirectory = directory;
            _lunaLibrary = string.IsNullOrEmpty(directory) ? null : new LunaGameLibraryProvider(directory);
            await RefreshLibraryAsync();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) when (IsIdentityFileError(error))
        {
            LibraryStatus = $"游戏库目录设置未保存：{error.Message}";
        }
        finally
        {
            if (pending is not null && File.Exists(pending)) File.Delete(pending);
            if (entered) _libraryConfigurationGate.Release();
        }
    }

    private async Task RefreshLibraryAsync()
    {
        try { await ReadLibraryAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_disposed) { }
        // Deliberately do not resolve/rebind here: reception and the active index retain their context.
    }

    private async Task<IReadOnlyList<GameLibraryEntry>> ReadLibraryAsync(CancellationToken token)
    {
        long request = ++_libraryGeneration;
        LunaGameLibraryProvider? provider = _lunaLibrary;
        if (provider is null)
        {
            LibraryStatus = "未配置 Luna 游戏库目录；已有本机映射仍然可用。";
            return [];
        }
        IReadOnlyList<GameLibraryEntry> games = await provider.GetGamesAsync(token);
        if (!_disposed && request == _libraryGeneration && ReferenceEquals(provider, _lunaLibrary)) LibraryStatus = provider.Status;
        return ReferenceEquals(provider, _lunaLibrary) ? games : [];
    }

    private async Task<(LocalGameMappings Mappings, string? Error)> ReadMappingsAsync(CancellationToken token)
    {
        try { return (await LocalGameMappings.LoadAsync(_mappingPath, token), null); }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return (LocalGameMappings.Empty, null); }
        catch (Exception error) when (IsIdentityFileError(error))
        { return (LocalGameMappings.Empty, "本机身份配置读取失败；不会自动覆盖原文件。"); }
    }

    private void ApplyIdentity(GameIdentityResult result)
    {
        _identitySummary = result.DisplayName == "未识别游戏" ? result.DisplayName : $"已识别：{result.DisplayName}";
        _requiredGameId = result.GameId;
        foreach (GameLibraryEntry entry in result.Candidates) IdentityCandidates.Add(entry);
        SelectedLibraryCandidate = result.Candidates.Count == 1 ? result.Candidates[0] : null;
        ShowIdentityDetails(result.VndbId, result.BangumiId, result.Source, result.GameId);
        OnPropertyChanged(nameof(GameIdentity));
        OnPropertyChanged(nameof(ActiveSummary));
        LoadCommand.NotifyCanExecuteChanged();
        ConfirmAssociationCommand.NotifyCanExecuteChanged();
    }

    private void ShowIdentityDetails(string? vndb, string? bangumi, string source, string? gameId) =>
        IdentityDetails = $"来源：{source} · VNDB：{vndb ?? "无"} · Bangumi：{bangumi ?? "无"}\n"
            + (gameId is null ? "尚未确认剧情 GameId；作品 ID 不能证明剧情节点兼容。" : $"已确认 GameId：{gameId}");

    private void ClearIdentityCandidates()
    {
        IdentityCandidates.Clear();
        SelectedLibraryCandidate = null;
        IdentityDetails = "尚无游戏身份。";
        ConfirmAssociationCommand.NotifyCanExecuteChanged();
    }

    private bool CanConfirmAssociation() => !_disposed && !_confirmingAssociation && !_isLoading && _windowBound
        && !string.IsNullOrWhiteSpace(_boundExecutablePath) && _requiredGameId is null
        && (IdentityCandidates.Count == 0 || SelectedLibraryCandidate is not null)
        && SelectedPack is { IsSelectable: true };

    private async Task ConfirmAssociationAsync()
    {
        if (!CanConfirmAssociation() || SelectedPack is not { } item || _boundExecutablePath is not { } path) return;
        long generation = _generation;
        GameLibraryEntry? entry = SelectedLibraryCandidate;
        string sourceMappingPath = _mappingPath;
        _confirmingAssociation = true;
        ConfirmAssociationCommand.NotifyCanExecuteChanged();
        try
        {
            // Validate the full pack before persisting its real GameId. No index is activated here.
            LoadedGamePack loaded = await Task.Run(() => _loader(item.Directory, _lifetime.Token), _lifetime.Token);
            if (IsStale(generation)) return;
            if (loaded.Pack.GameId != item.Entry.GameId || loaded.Index.GameId != loaded.Pack.GameId)
                throw new InvalidDataException("剧情包身份已变化，请刷新后重新确认。");
            ValidateRealWindowPack(loaded.Pack);
            string destination = Path.Combine(_identitySettingsDirectory, "game-mappings.local.json");
            var confirmation = new LocalGameMapping("local:" + Guid.NewGuid().ToString("N"),
                entry?.DisplayName ?? loaded.Pack.Name, Path.GetFileName(path), path, loaded.Pack.GameId,
                entry?.ProviderId, entry?.SourceGameId, entry?.WorkId, entry?.VndbId, entry?.BangumiId, ExactPathOnly: true);
            await LocalGameMappings.SaveConfirmationAsync(sourceMappingPath, destination, confirmation, _lifetime.Token);
            if (IsStale(generation) || sourceMappingPath != _mappingPath) return;
            string pointer = Path.Combine(_identitySettingsDirectory, "game-mapping-location.txt");
            await File.WriteAllTextAsync(pointer + ".tmp", "game-mappings.local.json", _lifetime.Token);
            if (IsStale(generation) || sourceMappingPath != _mappingPath)
            {
                File.Delete(pointer + ".tmp");
                return;
            }
            File.Move(pointer + ".tmp", pointer, overwrite: true);
            MappingPath = destination;
            if (!IsStale(generation)) await BindWindowAsync(path);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) when (IsIdentityFileError(error))
        {
            if (!IsStale(generation)) Status = $"关联未完成：{error.Message} 可继续使用 Luna 原文。";
        }
        finally
        {
            _confirmingAssociation = false;
            if (!_disposed) ConfirmAssociationCommand.NotifyCanExecuteChanged();
        }
    }

    private static void ValidateRealWindowPack(GamePack pack)
    {
        if (pack.TextSource.Kind == GamePackTextSourceKind.Mock
            || string.Equals(pack.Engine, "Synthetic", StringComparison.OrdinalIgnoreCase)
            || pack.GameId.Contains("synthetic", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("真实窗口不能关联模拟游戏包或模拟文本源。");
    }

    private static bool IsIdentityFileError(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
}
