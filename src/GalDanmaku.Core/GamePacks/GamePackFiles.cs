using System.Text;

namespace GalDanmaku.Core.GamePacks;

internal static class GamePackFiles
{
    public const string ManifestFileName = "gamepack.json";
    public const int MaximumRelativePathLength = 260;
    private static readonly string[] ReservedDeviceNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string NormalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Contains('\0'))
            throw new InvalidDataException("游戏包目录不能为空。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    // Validates the lexical form of a pack-relative path, then proves the resolved file stays inside
    // the pack directory even through symbolic links / reparse points.
    public static string ResolveInside(string root, string relative)
    {
        string[] segments = SplitRelative(relative);
        string full = Path.GetFullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar, segments)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException("路径超出游戏包目录。");

        string current = root;
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists) break;
            if (info.LinkTarget is null && !info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null && !Path.GetFullPath(target.FullName).StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
                throw new InvalidDataException("路径经符号链接或重解析点指向游戏包目录之外。");
        }
        return full;
    }

    private static string[] SplitRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > MaximumRelativePathLength)
            throw new InvalidDataException("相对路径为空或过长。");
        if (relative[0] is '/' or '\\' || Path.IsPathRooted(relative))
            throw new InvalidDataException("路径必须是游戏包内的相对路径，不能是绝对路径或 UNC 路径。");
        foreach (char character in relative)
            if (char.IsControl(character) || character is '<' or '>' or ':' or '"' or '|' or '?' or '*')
                throw new InvalidDataException("路径包含无效字符（含盘符、数据流或通配符）。");
        string[] segments = relative.Split('/', '\\');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment[^1] is '.' or ' ' || segment[0] == ' ')
                throw new InvalidDataException("路径含空段、“.”/“..”段，或以点、空格结尾的段。");
            if (ReservedDeviceNames.Contains(segment.Split('.')[0], StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("路径包含系统保留名称。");
        }
        return segments;
    }

    // Reads at most maximumBytes, checking the real byte count (not only Length) so a growing file cannot bypass it.
    public static async Task<string> ReadUtf8Async(string path, int maximumBytes, string what, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes) throw new InvalidDataException($"{what}超过 {maximumBytes / 1024} KiB 限制。");
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > maximumBytes) throw new InvalidDataException($"{what}超过 {maximumBytes / 1024} KiB 限制。");
            buffer.Write(chunk, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        try { return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException($"{what}必须使用 UTF-8 编码。", exception); }
    }
}
