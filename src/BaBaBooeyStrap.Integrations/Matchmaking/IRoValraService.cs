namespace BaBaBooeyStrap.Integrations.Matchmaking;

public record ServerLocationInfo(
    string Ip,
    string City,
    string Region,
    string Country,
    string RegionCode,
    double Latitude,
    double Longitude,
    int EstimatedPingMs
);

public interface IRoValraService
{
    Task<ServerLocationInfo?> ResolveServerLocationAsync(string ip, CancellationToken ct = default);

    Task<string?> GetBetterMatchmakingServerIdAsync(
        long placeId,
        bool randomize = false,
        CancellationToken ct = default) => Task.FromResult<string?>(null);

    Task<string?> GetBetterMatchmakingServerIdAsync(
        long placeId,
        bool randomize,
        int requiredSlots,
        IReadOnlySet<string>? excludedServerIds = null,
        CancellationToken ct = default) => GetBetterMatchmakingServerIdAsync(placeId, randomize, ct);

    Task<IReadOnlyList<string?>> AllocateBetterMatchmakingServersAsync(
        long placeId,
        int accountCount,
        bool randomize = false,
        CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string?>>(new string?[Math.Max(0, accountCount)]);
}
