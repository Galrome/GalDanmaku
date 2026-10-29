using System.Windows;
using System.Windows.Controls;
using GalDanmaku.App.ViewModels;
using GalDanmaku.App.Windows;
using Microsoft.Win32;

namespace GalDanmaku.App.Views;

public partial class ConnectionPage : UserControl
{
    public ConnectionPage() => InitializeComponent();

    private async void OnBrowseLunaLibrary(object sender, RoutedEventArgs args)
    {
        if (DataContext is not MainViewModel model) return;
        var dialog = new OpenFolderDialog { Title = "选择 LunaTranslator 安装目录或 userconfig 目录", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            await model.Pack.ConfigureLunaLibraryAsync(dialog.FolderName);
    }

    private async void OnApplyLunaLibrary(object sender, RoutedEventArgs args)
    {
        if (DataContext is MainViewModel model)
            await model.Pack.ConfigureLunaLibraryAsync(model.Pack.LunaLibraryDirectory);
    }

    private void OnAssociationSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (AssociationCompatibilityCheckBox is not null) AssociationCompatibilityCheckBox.IsChecked = false;
    }

    private async void OnWindowSelected(object sender, SelectionChangedEventArgs args)
    {
        if (AssociationCompatibilityCheckBox is not null) AssociationCompatibilityCheckBox.IsChecked = false;
        if (DataContext is not MainViewModel model || model.BindCommand.IsRunning
            || args.AddedItems.Count == 0 || args.AddedItems[0] is not WindowTarget target) return;
        if (model.IsBound && model.CurrentWindowSnapshot?.Target is { } current
            && current.Handle == target.Handle && current.ProcessId == target.ProcessId && current.ThreadId == target.ThreadId) return;
        if (model.IsBound) model.UnbindCommand.Execute(null);
        model.SelectedWindow = target;
        if (model.BindCommand.CanExecute(null)) await model.BindCommand.ExecuteAsync(null);
    }
}
