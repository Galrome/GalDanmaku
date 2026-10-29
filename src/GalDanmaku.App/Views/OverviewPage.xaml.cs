using System.Windows;
using System.Windows.Controls;

namespace GalDanmaku.App.Views;

public partial class OverviewPage : UserControl
{
    public OverviewPage() => InitializeComponent();

    public event EventHandler? ConnectionRequested;

    private void OnConnectionClick(object sender, RoutedEventArgs args) => ConnectionRequested?.Invoke(this, EventArgs.Empty);
}
