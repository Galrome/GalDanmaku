namespace GalDanmaku.Core.Diagnostics;

public sealed record LogContext(
    string? GameId = null,
    string? TextSessionId = null,
    long? DialogueId = null,
    long? Sequence = null,
    string? NodeId = null,
    string? OperationId = null);
