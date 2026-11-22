namespace BaBaBooeyStrap.Core.Roblox;

public sealed record AuthenticatedUser(long Id, string Name, string DisplayName);
public sealed record ResolvedShareLink(long PlaceId, string LinkCode);
public sealed record RobloxGameInfo(long PlaceId, long UniverseId, string Name, string? ThumbnailUrl);
public sealed record RobloxUserPresence(
    long UserId,
    int UserPresenceType,
    long? PlaceId,
    long? RootPlaceId,
    string? JobId,
    long? UniverseId,
    string? LastLocation
)
{
    public bool IsInGame => UserPresenceType == 2;
}

public interface IRobloxAuthService : IDisposable
{
    Task<AuthenticatedUser> GetAuthenticatedUserAsync(string cookie, CancellationToken ct = default);
    Task<string> GenerateAuthTicketAsync(string cookie, CancellationToken ct = default);
    Task<ResolvedShareLink> ResolveShareLinkAsync(string cookie, string shareCode, CancellationToken ct = default);
    Task<string?> GetAvatarThumbnailUrlAsync(long userId, string size = "150x150", bool isCircular = true, CancellationToken ct = default);
    Task<long> ResolveUserIdByUsernameAsync(string username, CancellationToken ct = default);
    Task<RobloxUserPresence> GetUserPresenceAsync(string cookie, long userId, CancellationToken ct = default);
    Task<RobloxGameInfo?> GetGameInfoByPlaceIdAsync(long placeId, CancellationToken ct = default);
}
