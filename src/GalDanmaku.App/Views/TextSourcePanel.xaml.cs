using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GalDanmaku.App.Views;

public partial class TextSourcePanel : UserControl
{
    public static readonly DependencyProperty StartReceptionCommandProperty = DependencyProperty.Register(
        nameof(StartReceptionCommand), typeof(ICommand), typeof(TextSourcePanel));

    public ICommand? StartReceptionCommand
    {
        get => (ICommand?)GetValue(StartReceptionCommandProperty);
        set => SetValue(StartReceptionCommandProperty, value);
    }

    public TextSourcePanel() => InitializeComponent();
}
