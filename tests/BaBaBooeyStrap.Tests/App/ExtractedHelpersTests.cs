using BaBaBooeyStrap.Core.Launch;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Integrations.Discord;
using BaBaBooeyStrap.Integrations.Watcher;

namespace BaBaBooeyStrap.Tests.App;

public sealed class ExtractedHelpersTests
{
    [Fact]
    public void ActivityWatcher_WithRobloxLogLineParser_TracksTeleportUdmuxAndRpcLaunchData()
    {
        using var watcher = new ActivityWatcher(options: new ActivityWatcherOptions { RpcRateLimit = TimeSpan.Zero });
        GameJoinEventArgs? joined = null;
        watcher.GameJoined += (_, e) => joined = e;

        watcher.ProcessLine("2026-11-21T12:00:00.000Z,0.1,Info doTeleport: joinScriptUrl=https://assetgame.roblox.com/game/PlaceLauncher.ashx?JoinTypeId%3a4");
        watcher.ProcessLine("2026-11-21T12:00:01.000Z,0.2,Info ! Joining game '11111111-2222-3333-4444-555555555555' place 1818 at 10.0.0.5");
        watcher.ProcessLine("2026-11-21T12:00:02.000Z,0.3,Info game_join_loadtime universeid:999 userid:42 referral_page:Normal");
        watcher.ProcessLine("2026-11-21T12:00:03.000Z,0.4,Info UDMUX Address = 128.116.50.1, Port = 53640 | RCC Server Address = 10.0.0.5, Port = 40000");
        watcher.ProcessLine("2026-11-21T12:00:04.000Z,0.5,Info serverId: 128.116.50.1|49152");
        watcher.ProcessLine("2026-11-21T12:00:05.000Z,0.6,Info [BloxstrapRPC] {\"command\":\"SetLaunchData\",\"data\":\"party-42\"}");
        watcher.ProcessLine("2026-11-21T12:00:06.000Z,0.7,Info [BloxstrapRPC] {\"command\":\"SetLaunchData\",\"data\":\"" + new string('x', 201) + "\"}");

        joined.Should().NotBeNull();
        joined!.PlaceId.Should().Be("1818");
        joined.ServerIp.Should().Be("128.116.50.1");
        joined.ServerPort.Should().Be(53640);
        joined.IsUdmux.Should().BeTrue();

        watcher.InGame.Should().BeTrue();
        watcher.CurrentActivity.Should().NotBeNull();
        watcher.CurrentActivity!.IsTeleport.Should().BeTrue();
        watcher.CurrentActivity.ServerType.Should().Be(ServerType.Reserved);
        watcher.CurrentActivity.UniverseId.Should().Be("999");
        watcher.CurrentActivity.UserId.Should().Be("42");
        watcher.CurrentActivity.RpcLaunchData.Should().Be("party-42");

        watcher.ProcessLine("2026-11-21T12:01:00.000Z,0.8,Info Time to disconnect replication data: 0.5");
        watcher.InGame.Should().BeFalse();
        watcher.History.Should().ContainSingle();
    }

    [Fact]
    public void DiscordRpcService_WithDiscordIpcProtocol_TruncatesUtf8RunesAndBuildsDeepLink()
    {
        // Multi-byte emoji string > 128 UTF-8 bytes trims cleanly at rune boundary
        string emojiHeavy = string.Concat(Enumerable.Repeat("🚀", 50));
        string formatted = DiscordRpcService.FormatPresenceText(emojiHeavy);
        Encoding.UTF8.GetByteCount(formatted).Should().BeLessOrEqualTo(128);
        formatted.Should().EndWith("🚀");

        // Single-char padding
        DiscordRpcService.FormatPresenceText("A", padSingleChar: true).Should().Be("A\u2800\u2800");

        // AccessCode takes precedence over JobId
        string accessDeepLink = DiscordRpcService.BuildRobloxDeepLink("1818", jobId: "job-1", accessCode: "vip-code", launchData: "abc");
        accessDeepLink.Should().Contain("accessCode=vip-code");
        accessDeepLink.Should().NotContain("gameInstanceId");
        accessDeepLink.Should().Contain("launchData=abc");
    }

    [Fact]
    public void RobloxLauncher_WithLaunchTargetParser_NormalizesDeepLinkAndParsesAllVariants()
    {
        string normalized = RobloxLauncher.NormalizeLaunchArgs("roblox://experiences/start?placeId=1818&gameInstanceId=job-99&launchData=hello");
        normalized.Should().StartWith("roblox-player:1+launchmode:play");

        var fromUri = RobloxLauncher.ParseTargetFromUri(normalized);
        fromUri.Kind.Should().Be(LaunchTargetKind.Standard);
        fromUri.PlaceId.Should().Be(1818);
        fromUri.JobId.Should().Be("job-99");

        var followByName = RobloxLauncher.ParseTargetInput("Builderman", followUserMode: true);
        followByName.Kind.Should().Be(LaunchTargetKind.FollowUser);
        followByName.Username.Should().Be("Builderman");

        string effectiveUrl = RobloxLauncher.BuildEffectiveTargetInput("1818", "job-77");
        effectiveUrl.Should().Be("https://www.roblox.com/games/1818?gameInstanceId=job-77");
    }
}
