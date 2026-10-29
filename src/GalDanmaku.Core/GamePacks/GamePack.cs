using GalDanmaku.Core.Story;
using GalDanmaku.Core.Text;

namespace GalDanmaku.Core.GamePacks;

public enum GamePackTextSourceKind { Mock, Luna }

public sealed record GamePackTextSource(GamePackTextSourceKind Kind, TextSourceOptions Options, IReadOnlyList<string> MockScript);

public sealed record LoadedGamePack(GamePack Pack, StoryIndex Index);

// Purely declarative description of a game. No code, script or regex is ever loaded from a pack.
public sealed class GamePack
{
    private readonly string[] _removeStrings;

    public string Directory { get; }
    public string GameId { get; }
    public string Name { get; }
    public string Version { get; }
    public string? Engine { get; }
    public IReadOnlyList<string> ProcessNames { get; }
    public GamePackTextSource TextSource { get; }

    internal GamePack(string directory, string gameId, string name, string version, string? engine,
        IReadOnlyList<string> processNames, GamePackTextSource textSource, IReadOnlyList<string> removeStrings)
    {
        Directory = directory;
        GameId = gameId;
        Name = name;
        Version = version;
        Engine = engine;
        ProcessNames = processNames;
        TextSource = textSource;
        _removeStrings = removeStrings.ToArray();
    }

    // Hint only: callers must not load a pack automatically because of a match.
    public bool MatchesProcess(string processName)
    {
        string? candidate = StripExecutableSuffix(processName?.Trim());
        if (string.IsNullOrEmpty(candidate)) return false;
        foreach (string name in ProcessNames)
            if (string.Equals(StripExecutableSuffix(name), candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Literal (non-regex) substring removal in manifest order. Over-long input is returned untouched
    // so the downstream DialogueProcessor rejects it instead of this method spending CPU on it.
    public string CleanText(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length > DialogueProcessor.MaximumTextLength) return raw;
        foreach (string remove in _removeStrings)
            raw = raw.Replace(remove, "", StringComparison.Ordinal);
        return raw;
    }

    private static string? StripExecutableSuffix(string? name)
        => name is not null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
