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
}
