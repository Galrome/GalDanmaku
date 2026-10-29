using System.Windows;
using System.Windows.Controls;
using GalDanmaku.App.ViewModels;
using Microsoft.Win32;

namespace GalDanmaku.App.Views;

public partial class StoryLocatorPanel : UserControl
{
    public StoryLocatorPanel() => InitializeComponent();

    private void OnBrowseIndex(object sender, RoutedEventArgs args)
    {
        if (DataContext is not StoryLocatorViewModel model || !model.CanImport) return;
        var dialog = new OpenFileDialog { Title = "选择剧情索引", Filter = "剧情索引 (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            model.IndexPath = dialog.FileName;
            model.LoadCommand.Execute(null);
        }
    }
}
