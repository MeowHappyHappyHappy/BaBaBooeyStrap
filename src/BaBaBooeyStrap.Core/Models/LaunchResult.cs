namespace BaBaBooeyStrap.Core.Models;

public sealed record LaunchResult(
    bool Success,
    string LaunchUri,
    LaunchTarget Target,
    DateTimeOffset LaunchedAt,
    int? ProcessId = null,
    string? ErrorMessage = null
);
