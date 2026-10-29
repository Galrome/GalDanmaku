using System.Windows;
using System.Windows.Controls;
using GalDanmaku.App.ViewModels;
using GalDanmaku.App.Windows;

namespace GalDanmaku.App.Views;

public partial class ConnectionPage : UserControl
{
    public ConnectionPage() => InitializeComponent();
    private async void OnWindowSelected(object sender, SelectionChangedEventArgs args)
    {
        if (DataContext is not MainViewModel model || model.BindCommand.IsRunning
            || args.AddedItems.Count == 0 || args.AddedItems[0] is not WindowTarget target) return;
        if (model.IsBound && model.CurrentWindowSnapshot?.Target is { } current
            && current.Handle == target.Handle && current.ProcessId == target.ProcessId && current.ThreadId == target.ThreadId) return;
        if (model.IsBound) model.UnbindCommand.Execute(null);
        model.SelectedWindow = target;
        if (model.BindCommand.CanExecute(null)) await model.BindCommand.ExecuteAsync(null);
    }
}
