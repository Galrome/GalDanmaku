using System.IO;
using GalDanmaku.Core.Diagnostics;

namespace GalDanmaku.App.ViewModels;

public sealed partial class MainViewModel
{
    private static string SettingsDirectory => Environment.GetEnvironmentVariable("GALDANMAKU_SETTINGS_DIRECTORY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalDanmaku");

    private static string? LoadMappingPath()
    {
        string? configured = Environment.GetEnvironmentVariable("GALDANMAKU_GAME_MAPPING_FILE");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        try
        {
            string pointer = Path.Combine(SettingsDirectory, "game-mapping-location.txt");
            return File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { RuntimeLog.Write(LogLevel.Warning, "GameMapping", "MappingLocationReadFailed", exception: error); return null; }
    }

    public async Task SelectMappingFileAsync(string path)
    {
        Pack.MappingPath = path;
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(Path.Combine(SettingsDirectory, "game-mapping-location.txt"), path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { RuntimeLog.Write(LogLevel.Warning, "GameMapping", "MappingLocationSaveFailed", exception: error); Status = "映射位置未能保存，本次仍使用所选文件。"; }
        if (IsBound && _lastSnapshot?.Target is { } target)
            await Pack.BindWindowAsync(target.ExecutablePath, target.ProcessIdentityError);
    }
}
