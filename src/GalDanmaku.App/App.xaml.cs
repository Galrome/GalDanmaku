using System.Windows;
using System.Windows.Threading;

namespace GalDanmaku.App;

public partial class App : Application
{
    private bool _showingError;

    public App() => DispatcherUnhandledException += OnDispatcherUnhandledException;

    // Last-resort net: tell the user instead of dying silently. A dialog pumps messages, so timer ticks can
    // fault again while it is open; further faults during that time are only swallowed from the crash path
    // (the dialog already told the user), never reported as success.
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_showingError) return;
        _showingError = true;
        try
        {
            MessageBox.Show($"发生未预期的错误：{e.Exception.GetType().Name}：{e.Exception.Message}\n\n程序将继续运行；若功能异常，请重启程序。已保存的弹幕不会丢失。",
                "GalDanmaku", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _showingError = false;
        }
    }
}
