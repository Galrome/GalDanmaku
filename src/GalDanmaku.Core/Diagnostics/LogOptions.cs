using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalDanmaku.Core.Diagnostics;

public sealed class LogOptions
{
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;
    public bool DetailedText { get; init; }
    public string? Directory { get; init; }
    public long MaxFileBytes { get; init; } = 10 * 1024 * 1024;
    public int RetentionDays { get; init; } = 7;
    public int QueueCapacity { get; init; } = 4096;
    public int MaxTextLength { get; init; } = 4096;
    public int MaxDataBytes { get; init; } = 32 * 1024;
    public int MaxFiles { get; init; } = 64;
    public long MaxTotalBytes { get; init; } = 256L * 1024 * 1024;

    public static LogOptions Load(string configPath)
    {
        var options = File.Exists(configPath)
            ? JsonSerializer.Deserialize<LogOptions>(File.ReadAllText(configPath), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                Converters = { new JsonStringEnumConverter<LogLevel>() },
            }) ?? throw new InvalidDataException("The logging configuration is empty.")
            : new LogOptions();

        var levelText = Environment.GetEnvironmentVariable("GALDANMAKU_LOG_LEVEL");
        var detailText = Environment.GetEnvironmentVariable("GALDANMAKU_LOG_DETAILED_TEXT");
        var directory = Environment.GetEnvironmentVariable("GALDANMAKU_LOG_DIRECTORY");
        var level = options.MinimumLevel;
        if (!string.IsNullOrWhiteSpace(levelText)
            && (!Enum.TryParse(levelText, true, out level) || !Enum.IsDefined(level)))
        {
            throw new InvalidDataException("GALDANMAKU_LOG_LEVEL is not a valid log level.");
        }

        var detailed = options.DetailedText;
        if (!string.IsNullOrWhiteSpace(detailText) && !bool.TryParse(detailText, out detailed))
        {
            throw new InvalidDataException("GALDANMAKU_LOG_DETAILED_TEXT must be true or false.");
        }

        var result = new LogOptions
        {
            MinimumLevel = level,
            DetailedText = detailed,
            Directory = string.IsNullOrWhiteSpace(directory) ? options.Directory : directory,
            MaxFileBytes = options.MaxFileBytes,
            RetentionDays = options.RetentionDays,
            QueueCapacity = options.QueueCapacity,
            MaxTextLength = options.MaxTextLength,
            MaxDataBytes = options.MaxDataBytes,
            MaxFiles = options.MaxFiles,
            MaxTotalBytes = options.MaxTotalBytes,
        };
        result.Validate();
        return result;
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(MinimumLevel)
            || MaxFileBytes < 64 * 1024 || MaxFileBytes > 1024L * 1024 * 1024
            || RetentionDays is < 1 or > 3650
            || QueueCapacity is < 1 or > 65536
            || MaxTextLength is < 1 or > 65536
            || MaxDataBytes is < 256 or > 1024 * 1024
            || MaxFiles is < 2 or > 10000
            || MaxTotalBytes < MaxFileBytes)
        {
            throw new InvalidDataException("Logging limits are outside the supported range.");
        }

        try
        {
            _ = ResolveDirectory();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("The logging directory is not a valid path.", error);
        }
    }

    internal string ResolveDirectory() => Path.GetFullPath(
        Directory ?? ApplicationDataPaths.LogsDirectory,
        AppContext.BaseDirectory);
}
