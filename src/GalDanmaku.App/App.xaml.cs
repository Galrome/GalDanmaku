using System.Windows;
using System.Windows.Threading;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using GalDanmaku.Core.Diagnostics;

namespace GalDanmaku.App;

public partial class App : Application
{
    private bool _showingError;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "logging.json");
        LogOptions options;
        Exception? configurationError = null;
        try { options = LogOptions.Load(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            options = new LogOptions();
            configurationError = exception;
        }
        RuntimeLog.Initialize(options);
        RuntimeLog.Write(LogLevel.Information, "App", "AppStarted", new
        {
            version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
#if DEBUG
            mode = "Debug",
#else
            mode = "Release",
#endif
            runtime = Environment.Version.ToString(), os = RuntimeInformation.OSDescription,
            processId = Environment.ProcessId,
        });
        RuntimeLog.Write(configurationError is null ? LogLevel.Information : LogLevel.Warning, "App",
            configurationError is null ? "LoggingConfigurationLoaded" : "LoggingConfigurationFailed",
            new { path, options.MinimumLevel, options.DetailedText, fallbackToDefaults = configurationError is not null }, configurationError);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RuntimeLog.Write(LogLevel.Information, "App", "AppStopped", new { exitCode = e.ApplicationExitCode });
        // WPF's final exit hook cannot be awaited. The logger drains on its own worker with a strict deadline.
        RuntimeLog.ShutdownAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        RuntimeLog.Write(LogLevel.Critical, "App", "UnhandledException", new { e.IsTerminating }, e.ExceptionObject as Exception);
        if (e.IsTerminating) RuntimeLog.ShutdownAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        RuntimeLog.Write(LogLevel.Error, "App", "UnobservedTaskException", exception: e.Exception);

    // Last-resort net: tell the user instead of dying silently. A dialog pumps messages, so timer ticks can
    // fault again while it is open; further faults during that time are only swallowed from the crash path
    // (the dialog already told the user), never reported as success.
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_showingError) return;
        RuntimeLog.Write(LogLevel.Error, "App", "DispatcherUnhandledException", exception: e.Exception);
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
