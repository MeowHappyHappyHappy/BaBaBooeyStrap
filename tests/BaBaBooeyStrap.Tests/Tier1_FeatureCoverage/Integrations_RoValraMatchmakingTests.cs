using BaBaBooeyStrap.Integrations.Matchmaking;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Integrations_RoValraMatchmakingTests
{
    private static void SetupGeoAndDataCenters(MockHttpMessageHandler handler)
    {
        handler.WhenGet("https://ipinfo.io/json").RespondWithJson(new
        {
            ip = "203.0.113.10",
            city = "Taipei",
            region = "Taipei",
            country = "TW",
            loc = "25.0330,121.5654"
        });

        handler.WhenGet("apis.rovalra.com/v1/datacenters/list").RespondWithJson(new[]
        {
            new { location = new { country = "JP", latLong = new[] { 35.6762, 139.6503 } } },
            new { location = new { country = "SG", latLong = new[] { 1.3521, 103.8198 } } }
        });
    }

    [Fact]
    public async Task LowPopulation_StaleRoValraServerNotInLiveList_FallsBackToNullForNativeMatchmaking()
    {
        var handler = new MockHttpMessageHandler();
        SetupGeoAndDataCenters(handler);

        // RoValra cache still has stale server IDs from a dead low-population server
        handler.WhenGet("apis.rovalra.com/v1/servers/region?place_id=5555&region=JP").RespondWithJson(new
        {
            servers = new[] { new { server_id = "stale-closed-srv" } }
        });
        handler.WhenGet("apis.rovalra.com/v1/servers/region?place_id=5555&region=SG").RespondWithJson(new
        {
            servers = Array.Empty<object>()
        });

        // Roblox live public server API shows 0 active servers (or different IDs)
        handler.WhenGet("games.roblox.com/v1/games/5555/servers/0").RespondWithJson(new
        {
            data = Array.Empty<object>()
        });

        using var http = handler.ToHttpClient();
        var service = new RoValraService(http);

        string? result = await service.GetBetterMatchmakingServerIdAsync(5555, randomize: false);
        result.Should().BeNull();
    }

    [Fact]
    public async Task FullServerSkipped_PicksNextLiveServerWithOpenSlot_OrReturnsNullWhenAllFull()
    {
        var handler = new MockHttpMessageHandler();
        SetupGeoAndDataCenters(handler);

        handler.WhenGet("apis.rovalra.com/v1/servers/region?place_id=7777&region=JP").RespondWithJson(new
        {
            servers = new[]
            {
                new { server_id = "full-srv" },
                new { server_id = "open-srv" }
            }
        });
        handler.WhenGet("apis.rovalra.com/v1/servers/region?place_id=7777&region=SG").RespondWithJson(new
        {
            servers = Array.Empty<object>()
        });

        handler.WhenGet("games.roblox.com/v1/games/7777/servers/0").RespondWithJson(new
        {
            data = new[]
            {
                new { id = "full-srv", playing = 4, maxPlayers = 4 },
                new { id = "open-srv", playing = 2, maxPlayers = 4 }
            }
        });

        using var http = handler.ToHttpClient();
        var service = new RoValraService(http);

        string? result = await service.GetBetterMatchmakingServerIdAsync(7777, randomize: false);
        result.Should().Be("open-srv");

        // Require 3 slots when open-srv only has 2 -> must return null
        string? insufficientResult = await service.GetBetterMatchmakingServerIdAsync(7777, randomize: false, requiredSlots: 3);
        insufficientResult.Should().BeNull();
    }

    [Fact]
    public async Task RobloxServersApiRateLimitedOrError_ConservativelyReturnsNull()
    {
        var handler = new MockHttpMessageHandler();
        SetupGeoAndDataCenters(handler);

        handler.WhenGet("apis.rovalra.com/v1/servers/region?place_id=8888&region=JP").RespondWithJson(new
        {
            servers = new[] { new { server_id = "cached-srv" } }
        });
        handler.WhenGet("games.roblox.com/v1/games/8888/servers/0")
            .RespondWithStatus(HttpStatusCode.TooManyRequests);

        using var http = handler.ToHttpClient();
        var service = new RoValraService(http);

        string? result = await service.GetBetterMatchmakingServerIdAsync(8888, randomize: false);
        result.Should().BeNull();

        IReadOnlyList<string?> batch = await service.AllocateBetterMatchmakingServersAsync(8888, accountCount: 3, randomize: false);
        batch.Should().HaveCount(3);
        batch.Should().AllSatisfy(id => id.Should().BeNull());
    }

    [Fact]
    public async Task BatchAllocation_PrefersSingleServerWithEnoughSlots_ThenSplitsAndFallsBackToNull()
    {
        // Case 1: Single server fits all 3 accounts
        var handlerSame = new MockHttpMessageHandler();
        SetupGeoAndDataCenters(handlerSame);
        handlerSame.WhenGet("apis.rovalra.com/v1/servers/region?place_id=1001&region=JP").RespondWithJson(new
        {
            servers = new[]
            {
                new { server_id = "jp-small" },
                new { server_id = "jp-big" }
            }
        });
        handlerSame.WhenGet("apis.rovalra.com/v1/servers/region?place_id=1001&region=SG").RespondWithJson(new
        {
            servers = Array.Empty<object>()
        });
        handlerSame.WhenGet("games.roblox.com/v1/games/1001/servers/0").RespondWithJson(new
        {
            data = new[]
            {
                new { id = "jp-small", playing = 3, maxPlayers = 4 }, // 1 slot
                new { id = "jp-big", playing = 1, maxPlayers = 5 }    // 4 slots >= 3
            }
        });

        using (var httpSame = handlerSame.ToHttpClient())
        {
            var svcSame = new RoValraService(httpSame);
            var allocSame = await svcSame.AllocateBetterMatchmakingServersAsync(1001, accountCount: 3);
            allocSame.Should().Equal("jp-big", "jp-big", "jp-big");
        }

        // Case 2: No single server fits 4 accounts -> splits across jp-a (2 slots), sg-b (1 slot), 4th falls back to null
        var handlerSplit = new MockHttpMessageHandler();
        SetupGeoAndDataCenters(handlerSplit);
        handlerSplit.WhenGet("apis.rovalra.com/v1/servers/region?place_id=1002&region=JP").RespondWithJson(new
        {
            servers = new[] { new { server_id = "jp-a" } }
        });
        handlerSplit.WhenGet("apis.rovalra.com/v1/servers/region?place_id=1002&region=SG").RespondWithJson(new
        {
            servers = new[] { new { server_id = "sg-b" } }
        });
        handlerSplit.WhenGet("games.roblox.com/v1/games/1002/servers/0").RespondWithJson(new
        {
            data = new[]
            {
                new { id = "jp-a", playing = 2, maxPlayers = 4 }, // 2 slots
                new { id = "sg-b", playing = 3, maxPlayers = 4 }  // 1 slot
            }
        });

        using var httpSplit = handlerSplit.ToHttpClient();
        var svcSplit = new RoValraService(httpSplit);
        var allocSplit = await svcSplit.AllocateBetterMatchmakingServersAsync(1002, accountCount: 4);
        allocSplit.Should().Equal("jp-a", "jp-a", "sg-b", null);
    }
}
