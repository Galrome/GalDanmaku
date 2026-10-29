using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalDanmaku.Core.Diagnostics;

namespace GalDanmaku.App.ViewModels;

public sealed partial class MainViewModel
{
    public IAsyncRelayCommand StartDemoCommand { get; private set; } = null!;
    public IAsyncRelayCommand StartTextReceptionCommand { get; private set; } = null!;

    private bool CanStartDemo => !_disposed && Pack.HasActivePack
        && TextSource.SelectedSource.IsMock && Story.CanLoad && Story.HasDemoFrames;

    private void InitializeDemo()
    {
        StartDemoCommand = new AsyncRelayCommand(StartDemoAsync, () => CanStartDemo);
        StartTextReceptionCommand = new AsyncRelayCommand(async () =>
        {
            using var context = RuntimeLog.BeginScope(new LogContext(GameId: _developerSession ? "developer-test" : null));
            Story.IsLiveMode = true;
            await TextSource.StartCommand.ExecuteAsync(null);
        }, () => !_disposed && TextSource.CanEdit);
        Story.PropertyChanged += OnDemoStoryChanged;
    }

    private void OnDemoStoryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(StoryLocatorViewModel.HasDemoFrames) or nameof(StoryLocatorViewModel.CanLoad))
            StartDemoCommand.NotifyCanExecuteChanged();
    }

    private async Task StartDemoAsync()
    {
        using var context = RuntimeLog.BeginScope(new LogContext(GameId: _developerSession ? "developer-test" : null));
        if (!CanStartDemo) return;
        var pack = Pack.ActivePack;
        await TextSource.StopAsync();
        // A pack switch or shutdown while stopping must not start a demo in the replacement context.
        if (!CanStartDemo || !ReferenceEquals(pack, Pack.ActivePack)) return;
        Story.IsLiveMode = false;
        Story.Reset();
        Story.NextDemoCommand.Execute(null);
    }
}
