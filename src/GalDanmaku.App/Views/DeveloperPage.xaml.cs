using System.Windows;
using System.Windows.Controls;
using GalDanmaku.App.ViewModels;
using Microsoft.Win32;

namespace GalDanmaku.App.Views;

public partial class DeveloperPage : UserControl
{
    public DeveloperPage() => InitializeComponent();
    private async void OnBrowseMapping(object sender, RoutedEventArgs args)
    {
        if (DataContext is not MainViewModel model) return;
        var dialog = new OpenFileDialog { Title = "选择本地私有游戏映射", Filter = "JSON (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await model.SelectMappingFileAsync(dialog.FileName);
    }

    private MainViewModel? Session => (DataContext as MainViewModel)?.TestSession;

    private async void OnBrowsePack(object sender, RoutedEventArgs args)
    {
        if (Session is not { } session) return;
        var dialog = new OpenFolderDialog { Title = "选择本地测试游戏适配包" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && ReferenceEquals(Session, session))
            await session.Pack.LoadPackAsync(dialog.FolderName);
    }

    private async void OnIndependentIndex(object sender, RoutedEventArgs args)
    {
        if (Session is not { } session) return;
        await session.Pack.ClearWindowBindingAsync();
        if (!ReferenceEquals(Session, session) || session.Pack.IsLoading || session.Pack.HasActivePack) return;
        session.TextSource.UseTestDefaults();
        session.Story.IsLiveMode = false;
    }

    private void OnBrowseIndex(object sender, RoutedEventArgs args)
    {
        if (Session?.Story is not { CanImport: true } story) return;
        var dialog = new OpenFileDialog { Title = "选择测试剧情索引", Filter = "JSON (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) story.IndexPath = dialog.FileName;
    }
}
