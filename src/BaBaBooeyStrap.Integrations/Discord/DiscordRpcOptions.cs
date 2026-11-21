using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.Integrations.Discord;

public sealed class DiscordRpcOptions
{
    public string ClientId { get; init; } = "1005469189907173486";
    public string? PipeNameOverride { get; init; }
    public string PipeNamePrefix { get; init; } = "discord-ipc";
    public int PipeScanStart { get; init; } = 0;
    public int PipeScanEnd { get; init; } = 9;
    public TimeSpan PerPipeConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(75);
    public TimeSpan DirectPipeConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan UpdateThrottle { get; init; } = TimeSpan.FromMilliseconds(50);
    public bool AutoReconnect { get; init; } = true;
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);
    public bool HideButtons { get; init; } = false;
    public DiscordRPCStatusDisplay StatusDisplayType { get; init; } = DiscordRPCStatusDisplay.Name;
    public string? GamePageUrl { get; init; }
}
