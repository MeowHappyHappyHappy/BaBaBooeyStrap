using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.Core.Models;

public class GameJoinData
{
    public GameJoinType JoinType { get; set; } = GameJoinType.Unknown;
    public string? JoinOrigin { get; set; }
    public long? PlaceId { get; set; }
    public string? JobId { get; set; }
    public long? UserId { get; set; }
    public string? AccessCode { get; set; }
    public string PlaceLauncherUrl { get; set; } = string.Empty;
}
