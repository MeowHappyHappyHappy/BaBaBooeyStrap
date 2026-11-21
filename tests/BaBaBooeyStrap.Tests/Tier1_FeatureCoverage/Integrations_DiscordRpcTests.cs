using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Integrations_DiscordRpcTests
{
    [Fact]
    public async Task F17_DiscordRpc_ConnectAndHandshake_SendsValidOpcode0Payload()
    {
        // Arrange
        string pipeName = $"test-discord-handshake-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var client = new DiscordRpcClient(pipeName);

        // Act
        bool connected = await client.ConnectAsync(TimeSpan.FromSeconds(3));

        // Assert
        connected.Should().BeTrue();
        client.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task F17_DiscordRpc_UpdatePresence_SendsSetActivityWithGameDetails()
    {
        // Arrange
        string pipeName = $"test-discord-activity-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var client = new DiscordRpcClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(3));

        // Act
        await client.SetActivityAsync("Playing Blox Fruits", "Level 2500");

        // Assert
        bool received = await server.WaitForMessageAsync(m => m.Contains("Playing Blox Fruits"), TimeSpan.FromSeconds(3));
        received.Should().BeTrue();
    }

    [Fact]
    public async Task F17_DiscordRpc_JoinButton_IncludesPlaceLaunchUri()
    {
        // Arrange
        string pipeName = $"test-discord-button-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var client = new DiscordRpcClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(3));

        // Act
        await client.SetActivityAsync("In Game", "Playing", placeId: 1818, hideButtons: false);

        // Assert
        bool received = await server.WaitForMessageAsync(m => m.Contains("roblox://experiences/start?placeId=1818"), TimeSpan.FromSeconds(3));
        received.Should().BeTrue();
    }

    [Fact]
    public async Task F17_DiscordRpc_HideButtons_OmitsButtonsArray()
    {
        // Arrange
        string pipeName = $"test-discord-nobutton-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var client = new DiscordRpcClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(3));

        // Act
        await client.SetActivityAsync("In Private Session", "Grinding", placeId: 1818, hideButtons: true);

        // Assert
        bool received = await server.WaitForMessageAsync(m => m.Contains("In Private Session"), TimeSpan.FromSeconds(3));
        received.Should().BeTrue();
        server.ReceivedMessages.Any(m => m.Contains("\"buttons\":")).Should().BeFalse();
    }

    [Fact]
    public async Task F17_DiscordRpc_ClearPresence_SendsEmptyActivityOnGameExit()
    {
        // Arrange
        string pipeName = $"test-discord-clear-{Guid.NewGuid():N}";
        using var server = new MockDiscordIpcServer(pipeName);
        server.Start();

        using var client = new DiscordRpcClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(3));

        // Act
        await client.ClearPresenceAsync();

        // Assert
        bool received = await server.WaitForMessageAsync(m => m.Contains("\"activity\":null"), TimeSpan.FromSeconds(3));
        received.Should().BeTrue();
    }
}
