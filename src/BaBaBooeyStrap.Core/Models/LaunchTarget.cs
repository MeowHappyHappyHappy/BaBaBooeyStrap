using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.Core.Models;

public sealed record LaunchTarget(
    LaunchTargetKind Kind,
    long? PlaceId,
    string? JobId = null,
    string? LinkCode = null,
    string? ShareCode = null,
    long? UserId = null,
    string? Username = null,
    string? AccessCode = null
);
