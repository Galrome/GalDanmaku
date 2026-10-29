using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GalDanmaku.App.Views;

public partial class TextSourcePanel : UserControl
{
    public static readonly DependencyProperty DemoCommandProperty = DependencyProperty.Register(
        nameof(DemoCommand), typeof(ICommand), typeof(TextSourcePanel));
    public static readonly DependencyProperty HasDemoFramesProperty = DependencyProperty.Register(
        nameof(HasDemoFrames), typeof(bool), typeof(TextSourcePanel));
    public static readonly DependencyProperty StartReceptionCommandProperty = DependencyProperty.Register(
        nameof(StartReceptionCommand), typeof(ICommand), typeof(TextSourcePanel));

    public ICommand? DemoCommand
    {
        get => (ICommand?)GetValue(DemoCommandProperty);
        set => SetValue(DemoCommandProperty, value);
    }

    public bool HasDemoFrames
    {
        get => (bool)GetValue(HasDemoFramesProperty);
        set => SetValue(HasDemoFramesProperty, value);
    }

    public ICommand? StartReceptionCommand
    {
        get => (ICommand?)GetValue(StartReceptionCommandProperty);
        set => SetValue(StartReceptionCommandProperty, value);
    }

    public TextSourcePanel() => InitializeComponent();
}
