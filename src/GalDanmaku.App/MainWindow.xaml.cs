using System.ComponentModel;
using GalDanmaku.App.ViewModels;
using Wpf.Ui.Controls;

namespace GalDanmaku.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private bool _closing;
    private bool _readyToClose;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.InitializeCommand.Execute(null);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_readyToClose) { base.OnClosing(e); return; }
        e.Cancel = true;
        base.OnClosing(e);
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        // The original canceled Closing event must unwind even when disposal completes synchronously.
        await Task.Yield();
        string? failure = null;
        try
        {
            await _viewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            // A failing teardown must not leave a disabled window that can never close.
            failure = $"{exception.GetType().Name}：{exception.Message}";
        }

        if (failure is not null)
            System.Windows.MessageBox.Show($"关闭时清理资源出错：{failure}\n已提交的弹幕不受影响。", "GalDanmaku",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        _readyToClose = true;
        Close();
    }
}
