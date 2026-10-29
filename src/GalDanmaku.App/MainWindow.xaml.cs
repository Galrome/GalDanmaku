using GalDanmaku.App.ViewModels;
using Wpf.Ui.Controls;

namespace GalDanmaku.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.InitializeCommand.Execute(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
