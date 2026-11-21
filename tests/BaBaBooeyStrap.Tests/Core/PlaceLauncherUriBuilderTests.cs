using BaBaBooeyStrap.Core.Roblox;

namespace BaBaBooeyStrap.Tests.Core;

public class PlaceLauncherUriBuilderTests
{
    [Fact]
    public void BuildPlaceLauncherUrl_StandardGame_ProducesExpectedUrl()
    {
        var builder = new PlaceLauncherUriBuilder
        {
            PlaceId = 1818
        };

        string url = builder.BuildPlaceLauncherUrl();
        url.Should().Be("https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame&placeId=1818");
    }

    [Fact]
    public void BuildPlaceLauncherUrl_JobId_ProducesRequestGameJob()
    {
        var builder = new PlaceLauncherUriBuilder
        {
            PlaceId = 1818,
            JobId = "550e8400-e29b-41d4-a716-446655440000"
        };

        string url = builder.BuildPlaceLauncherUrl();
        url.Should().Be("https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGameJob&placeId=1818&gameId=550e8400-e29b-41d4-a716-446655440000");
    }

    [Fact]
    public void BuildPlaceLauncherUrl_LinkCode_ProducesRequestPrivateGame()
    {
        var builder = new PlaceLauncherUriBuilder
        {
            PlaceId = 1818,
            LinkCode = "vip-link-12345"
        };

        string url = builder.BuildPlaceLauncherUrl();
        url.Should().Be("https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestPrivateGame&placeId=1818&linkCode=vip-link-12345");
    }

    [Fact]
    public void BuildPlaceLauncherUrl_AccessCode_ProducesRequestPrivateGame()
    {
        var builder = new PlaceLauncherUriBuilder
        {
            PlaceId = 1818,
            AccessCode = "access-code-999"
        };

        string url = builder.BuildPlaceLauncherUrl();
        url.Should().Be("https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestPrivateGame&placeId=1818&accessCode=access-code-999");
    }

    [Fact]
    public void BuildPlaceLauncherUrl_FollowUser_ProducesRequestFollowUser()
    {
        var builder = new PlaceLauncherUriBuilder
        {
            UserId = 12345678
        };

        string url = builder.BuildPlaceLauncherUrl();
        url.Should().Be("https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestFollowUser&userId=12345678");
    }

    [Fact]
    public void BuildPlaceLauncherUrl_WithLaunchData_EscapesSpecialCharacters()
    {
        var builder = new PlaceLauncherUriBuilder
        {
            PlaceId = 1818,
            LaunchData = "party=true&team=alpha+beta"
        };

        string url = builder.BuildPlaceLauncherUrl();
        url.Should().Contain("&launchData=" + Uri.EscapeDataString("party=true&team=alpha+beta"));
    }

    [Fact]
    public void BuildProtocolUri_ProducesValidCompliantUri()
    {
        var builder = new PlaceLauncherUriBuilder
        {
            PlaceId = 1818,
            AuthTicket = "ticket_xyz+123=",
            LaunchTimeMs = 1700000000000,
            RobloxLocale = "zh_tw",
            GameLocale = "zh_tw",
            BrowserTrackerId = "tracker-001",
            Channel = "live"
        };

        string uri = builder.BuildProtocolUri();

        uri.Should().StartWith("roblox-player:1+launchmode:play");
        uri.Should().Contain("+gameinfo:" + Uri.EscapeDataString("ticket_xyz+123="));
        uri.Should().Contain("+launchtime:1700000000000");
        uri.Should().Contain("+robloxLocale:zh_tw");
        uri.Should().Contain("+gameLocale:zh_tw");
        uri.Should().Contain("+browserTrackerId:tracker-001");
        uri.Should().Contain("+channel:live");
        uri.Any(char.IsWhiteSpace).Should().BeFalse();

        PlaceLauncherUriBuilder.IsValidLaunchUri(uri).Should().BeTrue();
    }

    [Fact]
    public void Parse_RoundTripsGeneratedUri()
    {
        var original = new PlaceLauncherUriBuilder
        {
            PlaceId = 999999,
            JobId = "guid-job-123",
            AuthTicket = "auth_ticket_token",
            LaunchTimeMs = 1700000000000,
            RobloxLocale = "en_us",
            GameLocale = "en_us",
            BrowserTrackerId = "browser-456",
            Channel = "zflag"
        };

        string uri = original.BuildProtocolUri();
        var parsed = PlaceLauncherUriBuilder.Parse(uri);

        parsed.PlaceId.Should().Be(999999);
        parsed.JobId.Should().Be("guid-job-123");
        parsed.AuthTicket.Should().Be("auth_ticket_token");
        parsed.LaunchTimeMs.Should().Be(1700000000000);
        parsed.RobloxLocale.Should().Be("en_us");
        parsed.GameLocale.Should().Be("en_us");
        parsed.BrowserTrackerId.Should().Be("browser-456");
        parsed.Channel.Should().Be("zflag");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://example.com")]
    [InlineData("roblox-player:1+has space in uri:invalid")]
    public void IsValidLaunchUri_RejectsInvalidUris(string invalidUri)
    {
        PlaceLauncherUriBuilder.IsValidLaunchUri(invalidUri).Should().BeFalse();
    }
}
