using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Integrations_RoValraTests
{
    [Fact]
    public async Task F20_RoValra_IpInfoLookup_ParsesCountryAndCity()
    {
        // Arrange
        const string testIp = "1.2.3.4";
        var handler = new MockHttpMessageHandler();
        handler.SetupRoute($"https://ipinfo.io/{testIp}/json", () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"country\":\"US\",\"city\":\"Ashburn\",\"region\":\"Virginia\"}", Encoding.UTF8, "application/json")
            });
        using var client = handler.ToHttpClient();

        // Act
        var geo = await RoValraService.GetGeoLocationAsync(testIp, client);

        // Assert
        geo.Country.Should().Be("US");
        geo.City.Should().Be("Ashburn");
        geo.Region.Should().Be("Virginia");
    }

    [Fact]
    public async Task F20_RoValra_ServerLatencyQuery_ReturnsRegionalPingMetrics()
    {
        // Arrange
        const string endpoint = "https://rovalra.api.mock/latencies";
        var handler = new MockHttpMessageHandler();
        handler.SetupRoute(endpoint, () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"us-east\": 35, \"us-west\": 85, \"eu-central\": 120}", Encoding.UTF8, "application/json")
            });
        using var client = handler.ToHttpClient();

        // Act
        var latencies = await RoValraService.GetRegionLatenciesAsync(endpoint, client);

        // Assert
        latencies.Count.Should().Be(3);
        latencies["us-east"].Should().Be(35);
        latencies["us-west"].Should().Be(85);
        latencies.OrderBy(kv => kv.Value).First().Key.Should().Be("us-east");
    }

    [Fact]
    public void F20_RoValra_LocationBadge_FormatsDisplayString()
    {
        // Arrange
        var geo = new GeoLocation("US", "Ashburn", "Virginia");

        // Act
        string badge = RoValraService.FormatLocationBadge(geo);

        // Assert
        badge.Should().Contain("🇺🇸");
        badge.Should().Contain("Ashburn");
    }

    [Fact]
    public async Task F20_RoValra_ApiFallback_ReturnsUnknownOnHttpError()
    {
        // Arrange
        const string testIp = "5.6.7.8";
        var handler = new MockHttpMessageHandler();
        handler.SetupRoute($"https://ipinfo.io/{testIp}/json", () =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = handler.ToHttpClient();

        // Act
        var geo = await RoValraService.GetGeoLocationAsync(testIp, client);

        // Assert
        geo.Should().Be(GeoLocation.Unknown);
    }

    [Fact]
    public void F20_RoValra_RandomizationToggled_AppliesJitterToMatchmaking()
    {
        // Arrange
        const int baseScore = 100;

        // Act 1: without jitter -> identical
        int scoreWithoutJitter = RoValraService.ApplyJitter(baseScore, enableJitter: false);
        scoreWithoutJitter.Should().Be(baseScore);

        // Act 2: with jitter -> within range
        int scoreWithJitter = RoValraService.ApplyJitter(baseScore, enableJitter: true, maxJitter: 15);
        scoreWithJitter.Should().BeInRange(85, 115);
    }
}
