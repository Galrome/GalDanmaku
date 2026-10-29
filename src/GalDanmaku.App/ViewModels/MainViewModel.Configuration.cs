using System.IO;
using System.Text.Json;
using GalDanmaku.Core;
using GalDanmaku.Core.Diagnostics;
using GalDanmaku.Core.GamePacks;

namespace GalDanmaku.App.ViewModels;

public sealed partial class MainViewModel
{
    private static string SettingsDirectory => ApplicationDataPaths.SettingsDirectory;

    public async Task SelectMappingFileAsync(string path)
    {
        string destination = Path.Combine(SettingsDirectory, "game-mappings.local.json");
        try
        {
            await LocalGameMappings.LoadAsync(path);
            Directory.CreateDirectory(SettingsDirectory);
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                string pending = destination + ".tmp";
                File.Copy(path, pending, overwrite: true);
                File.Move(pending, destination, overwrite: true);
            }
            // Keep this reference relative so copying the application folder preserves game recognition.
            File.WriteAllText(Path.Combine(SettingsDirectory, "game-mapping-location.txt"), "game-mappings.local.json");
            Pack.MappingPath = destination;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            RuntimeLog.Write(LogLevel.Warning, "GameMapping", "MappingImportFailed", exception: error);
            Status = $"识别配置导入失败：{error.Message}。请检查程序目录是否可写。";
            return;
        }
        if (IsBound && _lastSnapshot?.Target is { } target)
            await Pack.BindWindowAsync(target.ExecutablePath, target.ProcessIdentityError);
    }
}
