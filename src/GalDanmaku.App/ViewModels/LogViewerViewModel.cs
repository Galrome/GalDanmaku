using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalDanmaku.Core;
using GalDanmaku.Core.Diagnostics;
using Microsoft.Win32;

namespace GalDanmaku.App.ViewModels;

public sealed class LogViewerViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer;
    private string _level = "全部", _module = "全部", _search = "", _status = "只读取最近 8 个文件的尾部，最多显示 500 条记录。";
    private RuntimeLogRecord? _selected;
    private bool _redact = true;
    private IReadOnlyList<RuntimeLogRecord> _records = [];
    private ICollectionView _entries;

    public LogViewerViewModel()
    {
        _entries = CreateView(_records);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CopyCommand = new RelayCommand(Copy, () => Selected is not null);
        OpenDirectoryCommand = new RelayCommand(OpenDirectory);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += OnTick;
    }

    public IReadOnlyList<RuntimeLogRecord> Records => _records;
    public ICollectionView Entries { get => _entries; private set => SetProperty(ref _entries, value); }
    public IReadOnlyList<string> Levels { get; } = ["全部", "Trace", "Debug", "Information", "Warning", "Error", "Critical"];
    public ObservableCollection<string> Modules { get; } = ["全部"];
    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand CopyCommand { get; }
    public IRelayCommand OpenDirectoryCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public string DirectoryPath => RuntimeLog.DirectoryPath ?? ApplicationDataPaths.LogsDirectory;
    public string Level { get => _level; set { if (SetProperty(ref _level, value)) Entries.Refresh(); } }
    public string Module { get => _module; set { if (SetProperty(ref _module, value)) Entries.Refresh(); } }
    public string Search { get => _search; set { if (SetProperty(ref _search, value)) Entries.Refresh(); } }
    public bool Redact { get => _redact; set => SetProperty(ref _redact, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public RuntimeLogRecord? Selected { get => _selected; set { if (SetProperty(ref _selected, value)) { OnPropertyChanged(nameof(Detail)); CopyCommand.NotifyCanExecuteChanged(); } } }
    public string Detail => Selected?.Json ?? "选择一条日志查看详细字段。";

    public void Start() { if (_lifetime.IsCancellationRequested) return; _timer.Start(); OnTick(this, EventArgs.Empty); }
    public void Stop() => _timer.Stop();
    private void OnTick(object? sender, EventArgs args) { if (RefreshCommand.CanExecute(null)) RefreshCommand.Execute(null); }

    private ICollectionView CreateView(IReadOnlyList<RuntimeLogRecord> records) => new ListCollectionView(records.ToList())
    {
        Filter = value => value is RuntimeLogRecord record
            && (Level == "全部" || record.Level == Level) && (Module == "全部" || record.Module == Module)
            && (Search.Length == 0 || record.Json.Contains(Search, StringComparison.OrdinalIgnoreCase)),
    };

    private async Task RefreshAsync()
    {
        try
        {
            RuntimeLogSnapshot snapshot = await Task.Run(() => RuntimeLogReader.ReadRecent(DirectoryPath, _lifetime.Token), _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            string? selected = Selected?.Json;
            // Swap one bounded snapshot. Mutating an ObservableCollection inside DeferRefresh makes
            // ListCollectionView inspect CurrentPosition while refresh is deferred and throws on each tick.
            _records = snapshot.Records;
            Entries = CreateView(_records);
            OnPropertyChanged(nameof(Records));
            foreach (string module in Records.Select(record => record.Module).Distinct().Order())
                if (!Modules.Contains(module)) Modules.Add(module);
            Selected = Records.FirstOrDefault(record => record.Json == selected);
            Status = $"最近 {Records.Count} 条（最多 500）· 跳过格式错误 {snapshot.Skipped} 行 · 暂不可访问 {snapshot.UnavailableFiles} 个文件 · 每 4 秒刷新。"
                + (RuntimeLog.LastError is { } error ? $" 日志写入异常：{error}" : "");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { /* Page/application disposed. */ }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { Status = $"日志暂不可读：{error.Message}；下次刷新重试。"; }
        catch (InvalidOperationException error)
        {
            // A viewer failure must not become an endless DispatcherUnhandledException dialog loop.
            Stop();
            Status = $"日志显示失败，已暂停自动刷新：{error.Message}。可手动刷新重试。";
            RuntimeLog.Write(LogLevel.Error, "LogViewer", "RefreshFailed", exception: error);
        }
    }

    private void Copy()
    {
        if (Selected is not { } record) return;
        if (!Redact && !ConfirmPrivateExport()) return;
        try { Clipboard.SetText(record.Export(Redact)); Status = Redact ? "已复制脱敏日志。" : "已复制完整日志，请检查隐私后分享。"; }
        catch (ExternalException error) { Status = $"剪贴板暂不可用：{error.Message}"; }
    }

    private void OpenDirectory()
    {
        try { Directory.CreateDirectory(DirectoryPath); Process.Start(new ProcessStartInfo(DirectoryPath) { UseShellExecute = true }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        { Status = $"无法打开日志目录：{error.Message}"; }
    }

    private static bool ConfirmPrivateExport() => MessageBox.Show("完整日志可能含游戏台词、弹幕、本地路径或个人信息。请只向可信任的人提供，并在分享前检查内容。是否继续？",
        "诊断日志隐私", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private async Task ExportAsync()
    {
        bool redact = Redact;
        if (!redact && !ConfirmPrivateExport()) return;
        var dialog = new SaveFileDialog { Title = "导出当前筛选的近期诊断日志", Filter = "JSONL (*.jsonl)|*.jsonl",
            FileName = $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl", InitialDirectory = DirectoryPath };
        if (dialog.ShowDialog() != true) return;
        string[] lines = Entries.Cast<RuntimeLogRecord>().Select(record => record.Export(redact)).ToArray();
        try { await File.WriteAllLinesAsync(dialog.FileName, lines, _lifetime.Token); Status = $"已导出 {lines.Length} 条{(redact ? "脱敏" : "完整")}近期日志。"; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { /* Application closing. */ }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Status = $"导出失败：{error.Message}"; }
    }

    public void Dispose() { Stop(); _timer.Tick -= OnTick; _lifetime.Cancel(); }
}
