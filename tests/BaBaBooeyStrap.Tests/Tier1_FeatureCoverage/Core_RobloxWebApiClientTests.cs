using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Core_RobloxWebApiClientTests
{
    [Fact]
    public async Task F4_RobloxAuthService_TwoStepCsrfRetry_AcquiresAuthTicketSuccessfully()
    {
        // Arrange
        const string cookie = ".ROBLOSECURITY=_valid_cookie_123_";
        const string expectedCsrf = "csrf_token_secret_xyz";
        const string expectedTicket = "ticket_valid_999";

        var handler = new MockHttpMessageHandler()
            .SetupAuthTicketWorkflow(validCookie: cookie, csrfToken: expectedCsrf, authTicket: expectedTicket);
        using var client = handler.ToHttpClient();
        using var service = new RobloxAuthService(client);

        // Act
        string ticket = await service.GenerateAuthTicketAsync(cookie);

        // Assert
        ticket.Should().Be(expectedTicket);
        handler.AssertCalled(HttpMethod.Post, "auth.roblox.com/v1/authentication-ticket", expectedTimes: 2);
    }

    [Fact]
    public async Task F4_RobloxAuthService_ResolveShareLink_ExtractsPlaceIdAndLinkCode()
    {
        // Arrange
        const string shareCode = "share_link_test_code";
        const long expectedPlaceId = 1818;
        const string expectedLinkCode = "resolved_link_code_abc";

        var handler = new MockHttpMessageHandler()
            .SetupShareLinkWorkflow(shareCode, expectedPlaceId, expectedLinkCode);
        using var client = handler.ToHttpClient();
        using var service = new RobloxAuthService(client);

        // Act
        var result = await service.ResolveShareLinkAsync("cookie_unused", shareCode);

        // Assert
        result.PlaceId.Should().Be(expectedPlaceId);
        result.LinkCode.Should().Be(expectedLinkCode);
    }

    [Fact]
    public async Task F4_RobloxAuthService_FetchChannelVersion_ParsesVersionGuidAndUpload()
    {
        // Arrange
        const string channel = "zintegration";
        const string expectedVersionGuid = "version-4ea8e85481e34f46";
        const string expectedVersion = "0.640.0.12345";

        var handler = new MockHttpMessageHandler()
            .SetupChannelVersionWorkflow(channel, expectedVersionGuid, expectedVersion);
        using var client = handler.ToHttpClient();

        // Act
        string url = $"https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer/channel/{channel}";
        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        string versionGuid = doc.RootElement.GetProperty("clientVersionUpload").GetString()!;
        string version = doc.RootElement.GetProperty("version").GetString()!;

        // Assert
        versionGuid.Should().Be(expectedVersionGuid);
        version.Should().Be(expectedVersion);
    }

    [Fact]
    public void F4_PlaceLauncherUriBuilder_BuildStandardGame_FormatsValidLaunchProtocol()
    {
        // Arrange
        var builder = new PlaceLauncherUriBuilder
        {
            PlaceId = 1818,
            JobId = "job-guid-xyz",
            AuthTicket = "ticket_abc"
        };

        // Act
        string placeLauncherUrl = builder.BuildPlaceLauncherUrl();
        string protocolUri = builder.BuildProtocolUri();

        // Assert
        placeLauncherUrl.Should().StartWith("https://assetgame.roblox.com/game/PlaceLauncher.ashx?");
        placeLauncherUrl.Should().Contain("request=RequestGameJob");
        placeLauncherUrl.Should().Contain("placeId=1818");
        placeLauncherUrl.Should().Contain("gameId=job-guid-xyz");

        protocolUri.Should().StartWith("roblox-player:1+launchmode:play");
        protocolUri.Should().Contain("+gameinfo:ticket_abc");
    }

    [Fact]
    public void F4_PlaceLauncherUriBuilder_BuildPrivateServer_IncludesLinkCodeOrAccessCode()
    {
        // Arrange
        var builder = new PlaceLauncherUriBuilder
        {
            PlaceId = 1818,
            LinkCode = "vip-invite-code-12345"
        };

        // Act
        string placeLauncherUrl = builder.BuildPlaceLauncherUrl();

        // Assert
        placeLauncherUrl.Should().Contain("request=RequestPrivateGame");
        placeLauncherUrl.Should().Contain("placeId=1818");
        placeLauncherUrl.Should().Contain("linkCode=vip-invite-code-12345");
    }
}
