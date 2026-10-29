using System.Windows;
using System.Windows.Controls;
using GalDanmaku.App.ViewModels;

namespace GalDanmaku.App.Views;

public partial class DiagnosticsPage : UserControl
{
    public DiagnosticsPage() => InitializeComponent();
    private void OnLoaded(object sender, RoutedEventArgs args) => (DataContext as MainViewModel)?.Logs?.Start();
    private void OnUnloaded(object sender, RoutedEventArgs args) => (DataContext as MainViewModel)?.Logs?.Stop();
    private async void OnDeveloperModeClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is MainViewModel model) await model.SetDeveloperModeAsync(!model.IsDeveloperMode);
    }
}
