using BaBaBooeyStrap.Integrations.Matchmaking;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Integrations;

public sealed class RoValraServiceTests
{
    [Fact]
    public async Task ResolveServerLocationAsync_IpInfoResponse_ParsesLocationCoordsAndRegionCode()
    {
        var handler = new MockHttpMessageHandler();
        handler.WhenGet("ipinfo.io/128.116.119.5/json").RespondWithJson(new
        {
            ip = "128.116.119.5",
            city = "Ashburn",
            region = "Virginia",
            country = "US",
            loc = "39.0438,-77.4874"
        });

        using var http = handler.ToHttpClient();
        var service = new RoValraService(http, clientLatitude: 40.7128, clientLongitude: -74.0060); // NYC -> Ashburn

        var result = await service.ResolveServerLocationAsync("128.116.119.5");

        result.Should().NotBeNull();
        result!.Ip.Should().Be("128.116.119.5");
        result.City.Should().Be("Ashburn");
        result.Region.Should().Be("Virginia");
        result.Country.Should().Be("US");
        result.RegionCode.Should().Be("US-East");
        result.Latitude.Should().BeApproximately(39.0438, 0.001);
        result.Longitude.Should().BeApproximately(-77.4874, 0.001);
        result.EstimatedPingMs.Should().BeGreaterThan(0);

        RoValraService.FormatLocationBadge(result).Should().Be("🇺🇸 Ashburn");
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.50")]
    [InlineData("127.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("::127.0.0.1")]
    [InlineData("::10.0.0.1")]
    [InlineData("::192.168.1.1")]
    [InlineData("not-an-ip")]
    [InlineData("")]
    public async Task ResolveServerLocationAsync_PrivateOrBogonOrInvalidIp_ReturnsNullWithoutHttpCalls(string badIp)
    {
        var handler = new MockHttpMessageHandler();
        using var http = handler.ToHttpClient();
        var service = new RoValraService(http);

        var result = await service.ResolveServerLocationAsync(badIp);

        result.Should().BeNull();
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public void RegionCodeMappingAndFlagBadges_ResolveExpectedCanonicalValues()
    {
        RoValraService.ResolveRegionCode("US", "Virginia").Should().Be("US-East");
        RoValraService.ResolveRegionCode("US", "California").Should().Be("US-West");
        RoValraService.ResolveRegionCode("US", "Illinois").Should().Be("US-Central");
        RoValraService.ResolveRegionCode("DE", "Hesse").Should().Be("EU-Central");
        RoValraService.ResolveRegionCode("GB", "England").Should().Be("EU-West");
        RoValraService.ResolveRegionCode("JP", "Tokyo").Should().Be("AP-East");
        RoValraService.ResolveRegionCode("SG", "Singapore").Should().Be("AP-Southeast");
        RoValraService.ResolveRegionCode("BR", "Sao Paulo").Should().Be("SA-East");
        RoValraService.ResolveRegionCode("Unknown", "").Should().Be("Unknown");

        RoValraService.FormatFlagEmoji("US").Should().Be("🇺🇸");
        RoValraService.FormatFlagEmoji("GB").Should().Be("🇬🇧");
        RoValraService.FormatFlagEmoji("DE").Should().Be("🇩🇪");
        RoValraService.FormatFlagEmoji("JP").Should().Be("🇯🇵");
        RoValraService.FormatFlagEmoji("??").Should().Be("🌐");
    }

    [Fact]
    public async Task PingEstimation_UsesIcmpWhenAvailable_FallsBackToHaversineOnTimeout()
    {
        var handler = new MockHttpMessageHandler();
        handler.WhenGet("ipinfo.io/128.116.50.10/json").RespondWithJson(new
        {
            ip = "128.116.50.10",
            city = "Frankfurt",
            region = "Hesse",
            country = "DE",
            loc = "50.1109,8.6821"
        });

        using var http = handler.ToHttpClient();

        // 1) ICMP probe succeeds
        var activeProbe = new StubPingProbe(42);
        var serviceWithIcmp = new RoValraService(http, activeProbe, clientLatitude: 51.5074, clientLongitude: -0.1278);
        var withIcmp = await serviceWithIcmp.ResolveServerLocationAsync("128.116.50.10");
        withIcmp!.EstimatedPingMs.Should().Be(42);
        withIcmp.RegionCode.Should().Be("EU-Central");

        // 2) ICMP probe times out (null) -> Haversine fallback (London -> Frankfurt ~637 km => ~18ms)
        var timeoutProbe = new StubPingProbe(null);
        var serviceWithFallback = new RoValraService(http, timeoutProbe, clientLatitude: 51.5074, clientLongitude: -0.1278);
        var fallback = await serviceWithFallback.ResolveServerLocationAsync("128.116.50.10");
        fallback!.EstimatedPingMs.Should().BeInRange(15, 25);
    }

    [Fact]
    public async Task HttpErrorResilience_And_RoValraGeolocationFallback_HandleFailuresGracefully()
    {
        var handler = new MockHttpMessageHandler();

        // 1) IPInfo returns 502 HTML error, RoValra geolocation succeeds
        handler.WhenGet("ipinfo.io/128.116.200.1/json")
            .RespondWithString("<html>502 Bad Gateway</html>", "text/html", HttpStatusCode.BadGateway);
        handler.WhenGet("apis.rovalra.com/v1/geolocation?ip=128.116.200.1")
            .RespondWithJson(new
            {
                location = new
                {
                    city = "Tokyo",
                    country_name = "Japan",
                    region = "Tokyo"
                }
            });

        // 2) Both fail (429 / 500) -> returns null gracefully
        handler.WhenGet("ipinfo.io/8.8.4.4/json").RespondWithStatus(HttpStatusCode.TooManyRequests);
        handler.WhenGet("apis.rovalra.com/v1/geolocation?ip=8.8.4.4").RespondWithStatus(HttpStatusCode.InternalServerError);

        // 3) Bogon response from IPInfo -> returns null
        handler.WhenGet("ipinfo.io/198.51.100.1/json").RespondWithJson(new { ip = "198.51.100.1", bogon = true });

        using var http = handler.ToHttpClient();
        var service = new RoValraService(http);

        var roValraResult = await service.ResolveServerLocationAsync("128.116.200.1");
        roValraResult.Should().NotBeNull();
        roValraResult!.City.Should().Be("Tokyo");
        roValraResult.Country.Should().Be("JP");
        roValraResult.RegionCode.Should().Be("AP-East");

        var errorResult = await service.ResolveServerLocationAsync("8.8.4.4");
        errorResult.Should().BeNull();

        var bogonResult = await service.ResolveServerLocationAsync("198.51.100.1");
        bogonResult.Should().BeNull();
    }

    [Fact]
    public async Task GetBetterMatchmakingServerIdAsync_SupportsNumericAndStringCoordsAndSelectsBestRegion()
    {
        var handler = new MockHttpMessageHandler();
        handler.WhenGet("https://ipinfo.io/json").RespondWithJson(new
        {
            ip = "203.0.113.10",
            city = "Ashburn",
            region = "Virginia",
            country = "US",
            loc = "39.0438,-77.4874"
        });

        // Mix numeric and string latLong values to verify both are parsed cleanly
        handler.WhenGet("apis.rovalra.com/v1/datacenters/list").RespondWithString(
            """
            [
              { "location": { "country": "DE", "latLong": [50.1109, 8.6821] } },
              { "location": { "country": "US", "latLong": ["37.7749", "-122.4194"] } }
            ]
            """,
            "application/json");

        handler.WhenGet("apis.rovalra.com/v1/servers/region?place_id=1818&region=US").RespondWithJson(new
        {
            servers = new[]
            {
                new { server_id = "srv-us-1111" },
                new { server_id = "srv-us-2222" }
            }
        });

        using var http = handler.ToHttpClient();
        var service = new RoValraService(http);

        string? serverId = await service.GetBetterMatchmakingServerIdAsync(1818, randomize: false);
        serverId.Should().Be("srv-us-1111");

        // Graceful degradation when servers endpoint returns HTTP 429
        handler.WhenGet("apis.rovalra.com/v1/servers/region?place_id=9999&region=US")
            .RespondWithStatus(HttpStatusCode.TooManyRequests);
        (await service.GetBetterMatchmakingServerIdAsync(9999)).Should().BeNull();
    }

    [Fact]
    public async Task GetRegionLatenciesAsync_ParsesValidLatenciesAndHandlesHttpErrors()
    {
        var handler = new MockHttpMessageHandler();
        handler.WhenGet("apis.rovalra.com/v1/latencies").RespondWithJson(new
        {
            US_East = 24,
            EU_West = 95,
            InvalidNegative = -5,
            InvalidTooHigh = 6000
        });
        handler.WhenGet("apis.rovalra.com/v1/latencies-bad").RespondWithStatus(HttpStatusCode.BadGateway);

        using var http = handler.ToHttpClient();
        var service = new RoValraService(http);

        var latencies = await service.GetRegionLatenciesAsync("https://apis.rovalra.com/v1/latencies");
        latencies.Should().ContainKey("US_East").WhoseValue.Should().Be(24);
        latencies.Should().ContainKey("EU_West").WhoseValue.Should().Be(95);
        latencies.Should().NotContainKey("InvalidNegative");
        latencies.Should().NotContainKey("InvalidTooHigh");

        var bad = await service.GetRegionLatenciesAsync("https://apis.rovalra.com/v1/latencies-bad");
        bad.Should().BeEmpty();
    }

    private sealed class StubPingProbe : IPingProbe
    {
        private readonly int? _result;
        public StubPingProbe(int? result) => _result = result;
        public Task<int?> PingAsync(string ip, int timeoutMs, CancellationToken ct = default) => Task.FromResult(_result);
    }
}
