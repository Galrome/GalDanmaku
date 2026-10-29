using GalDanmaku.Core.Diagnostics;

namespace GalDanmaku.App.ViewModels;

public sealed partial class MainViewModel
{
    public async Task SetDeveloperModeAsync(bool enabled)
    {
        if (_disposed || _testSessionBusy || enabled == IsDeveloperMode) return;
        // End and await the separate source/overlay before hiding its navigation entry.
        if (!enabled) await EndTestSessionAsync();
        IsDeveloperMode = enabled;
        BeginTestSessionCommand.NotifyCanExecuteChanged();
        RuntimeLog.Write(LogLevel.Information, "Developer", "DeveloperVisibilityChanged", new { enabled });
    }

    private async Task BeginTestSessionAsync()
    {
        if (!IsDeveloperMode || _disposed || TestSession is not null || _testSessionBusy) return;
        _testSessionBusy = true;
        OnPropertyChanged(nameof(CanChangeDeveloperMode));
        BeginTestSessionCommand.NotifyCanExecuteChanged();
        try
        {
            TestSession = new MainViewModel(developerSession: true);
            await TestSession.InitializeCommand.ExecuteAsync(null);
            Status = "独立测试会话已开始；正常游戏的 Luna 与剧情会话继续保留。";
        }
        finally
        {
            _testSessionBusy = false;
            OnPropertyChanged(nameof(CanChangeDeveloperMode));
            BeginTestSessionCommand.NotifyCanExecuteChanged();
            EndTestSessionCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task EndTestSessionAsync()
    {
        if (TestSession is not { } test || _testSessionBusy) return;
        _testSessionBusy = true;
        OnPropertyChanged(nameof(CanChangeDeveloperMode));
        EndTestSessionCommand.NotifyCanExecuteChanged();
        try
        {
            TestSession = null;
            await test.DisposeAsync();
            Status = "独立测试会话已停止，测试窗口覆盖层已关闭；正常游戏会话继续保留。";
        }
        finally
        {
            _testSessionBusy = false;
            OnPropertyChanged(nameof(CanChangeDeveloperMode));
            BeginTestSessionCommand.NotifyCanExecuteChanged();
            EndTestSessionCommand.NotifyCanExecuteChanged();
        }
    }

    public bool CanChangeDeveloperMode => !_disposed && !_testSessionBusy;
}
