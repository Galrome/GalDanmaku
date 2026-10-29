namespace GalDanmaku.Core.Story;

public enum StoryLocationStatus { Unknown, Ambiguous, Confirmed }

public sealed record StoryLocation(StoryLocationStatus Status, StoryNode? Node,
    IReadOnlyList<StoryNode> Candidates, string Reason);
