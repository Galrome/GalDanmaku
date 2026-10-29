namespace GalDanmaku.Core;

// Portable user data travels with the executable. Explicit overrides are reserved for isolated development runs.
public static class ApplicationDataPaths
{
    public static string DataDirectory => ResolveDataDirectory(AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("GALDANMAKU_DATA_DIRECTORY"));
    public static string SettingsDirectory => Path.GetFullPath(
        Environment.GetEnvironmentVariable("GALDANMAKU_SETTINGS_DIRECTORY") ?? DataDirectory, AppContext.BaseDirectory);
    public static string DatabasePath => Path.Combine(DataDirectory, "danmaku.db");
    public static string DeveloperDatabasePath => Path.Combine(DataDirectory, "developer", "danmaku.db");
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string ResolveDataDirectory(string applicationDirectory, string? overrideDirectory = null) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(overrideDirectory) ? "data" : overrideDirectory, applicationDirectory);
}
