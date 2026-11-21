namespace BaBaBooeyStrap.Integrations.Discord;

public record DiscordActivity(
    string Details,
    string State,
    string? LargeImageKey,
    string? LargeImageText,
    string? SmallImageKey,
    string? SmallImageText,
    long? StartTimestamps,
    string? JoinSecret,
    string? DeepLinkUrl
);

public interface IDiscordRpcService : IDisposable
{
    bool IsConnected { get; }
    Task<bool> ConnectAsync(CancellationToken ct = default);
    Task UpdatePresenceAsync(DiscordActivity activity, CancellationToken ct = default);
    Task ClearPresenceAsync(CancellationToken ct = default);
    void Disconnect();
}
