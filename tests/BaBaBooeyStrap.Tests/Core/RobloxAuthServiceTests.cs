using System.Net;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Core;

public class RobloxAuthServiceTests
{
    [Fact]
    public async Task GetAuthenticatedUserAsync_Success()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.WhenGet("users.roblox.com/v1/users/authenticated")
            .RespondWithJson(new
            {
                id = 987654321L,
                name = "TestEngineer",
                displayName = "Chief Engineer"
            });

        using var service = new RobloxAuthService(mockHandler.ToHttpClient());
        var user = await service.GetAuthenticatedUserAsync("test_cookie");

        user.Id.Should().Be(987654321L);
        user.Name.Should().Be("TestEngineer");
        user.DisplayName.Should().Be("Chief Engineer");

        mockHandler.AssertCalled(HttpMethod.Get, "users.roblox.com/v1/users/authenticated", 1);
        mockHandler.AssertHeader("users.roblox.com/v1/users/authenticated", "Cookie", ".ROBLOSECURITY=test_cookie");
    }

    [Fact]
    public async Task GenerateAuthTicketAsync_TwoStepCsrfRetry_HeaderSuccess()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupAuthTicketWorkflow(
            validCookie: "test_cookie",
            csrfToken: "challenge_csrf_token_xyz",
            authTicket: "ticket_obtained_successfully"
        );

        using var service = new RobloxAuthService(mockHandler.ToHttpClient());
        string ticket = await service.GenerateAuthTicketAsync("test_cookie");

        ticket.Should().Be("ticket_obtained_successfully");

        // Assert 2 requests: initial 403 challenge, then retry with X-CSRF-TOKEN
        mockHandler.AssertCalled(HttpMethod.Post, "auth.roblox.com/v1/authentication-ticket", 2);
    }

    [Fact]
    public async Task GenerateAuthTicketAsync_TwoStepCsrfRetry_BodyFallback()
    {
        var mockHandler = new MockHttpMessageHandler();
        int callCount = 0;

        mockHandler.WhenPost("auth.roblox.com/v1/authentication-ticket")
            .RespondWith((req, ct) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var challenge = new HttpResponseMessage(HttpStatusCode.Forbidden);
                    challenge.Headers.Add("x-csrf-token", "body_csrf_token");
                    return Task.FromResult(challenge);
                }

                // Call 2: No header, body contains ticket property
                var ok = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ticket\":\"fallback_ticket_123\"}", Encoding.UTF8, "application/json")
                };
                return Task.FromResult(ok);
            });

        using var service = new RobloxAuthService(mockHandler.ToHttpClient());
        string ticket = await service.GenerateAuthTicketAsync("test_cookie");

        ticket.Should().Be("fallback_ticket_123");
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task ResolveShareLinkAsync_Success_NestedPrivateServerData()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.WhenPost("apis.roblox.com/sharelinks/v1/resolve-link")
            .RespondWithJson(new
            {
                statusCode = 200,
                privateServerInviteData = new
                {
                    placeId = 1818L,
                    linkCode = "nested_vip_link_code_abc"
                }
            });

        using var service = new RobloxAuthService(mockHandler.ToHttpClient());
        var resolved = await service.ResolveShareLinkAsync("test_cookie", "share_code_123");

        resolved.PlaceId.Should().Be(1818L);
        resolved.LinkCode.Should().Be("nested_vip_link_code_abc");
    }

    [Fact]
    public async Task GetAvatarThumbnailUrlAsync_Success()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.WhenGet("thumbnails.roblox.com/v1/users/avatar-headshot")
            .RespondWithJson(new
            {
                data = new[]
                {
                    new { targetId = 12345L, state = "Completed", imageUrl = "https://tr.rbxcdn.com/avatar_test.png" }
                }
            });

        using var service = new RobloxAuthService(mockHandler.ToHttpClient());
        string? url = await service.GetAvatarThumbnailUrlAsync(12345L);

        url.Should().Be("https://tr.rbxcdn.com/avatar_test.png");
    }

    [Fact]
    public async Task GetAuthenticatedUserAsync_Unauthorized_ThrowsRobloxApiException()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.WhenGet("users.roblox.com/v1/users/authenticated")
            .RespondWithStatus(HttpStatusCode.Unauthorized);

        using var service = new RobloxAuthService(mockHandler.ToHttpClient());
        var act = async () => await service.GetAuthenticatedUserAsync("invalid_cookie");

        var ex = await act.Should().ThrowAsync<RobloxApiException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResolveUserIdByUsernameAsync_StripsAtPrefixAndResolvesId_OrThrowsWhenNotFound()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.WhenPost("users.roblox.com/v1/usernames/users")
            .RespondWith(async (req, ct) =>
            {
                string reqBody = await req.Content!.ReadAsStringAsync(ct);
                if (reqBody.Contains("Builderman", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":[{\"requestedUsername\":\"Builderman\",\"hasVerifiedBadge\":true,\"id\":156,\"name\":\"Builderman\",\"displayName\":\"Builderman\"}]}", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
                };
            });

        using var service = new RobloxAuthService(mockHandler.ToHttpClient());

        long resolvedId = await service.ResolveUserIdByUsernameAsync("  @Builderman  ");
        resolvedId.Should().Be(156L);

        var actNotFound = async () => await service.ResolveUserIdByUsernameAsync("NonExistentUser999");
        var ex = await actNotFound.Should().ThrowAsync<RobloxApiException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUserPresenceAsync_ParsesInGameWithJobId_InGameHiddenJobId_AndOffline()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.WhenPost("presence.roblox.com/v1/presence/users")
            .RespondWith(async (req, ct) =>
            {
                string reqBody = await req.Content!.ReadAsStringAsync(ct);
                if (reqBody.Contains("156"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"userPresences\":[{\"userPresenceType\":2,\"lastLocation\":\"Playing Blox Fruits\",\"placeId\":2753915549,\"rootPlaceId\":2753915549,\"gameId\":\"11111111-2222-3333-4444-555555555555\",\"universeId\":994732206,\"userId\":156}]}",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                if (reqBody.Contains("777"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"userPresences\":[{\"userPresenceType\":2,\"lastLocation\":\"Playing\",\"placeId\":null,\"rootPlaceId\":null,\"gameId\":null,\"universeId\":null,\"userId\":777}]}",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"userPresences\":[{\"userPresenceType\":1,\"lastLocation\":\"Website\",\"placeId\":null,\"rootPlaceId\":null,\"gameId\":null,\"universeId\":null,\"userId\":888}]}",
                        Encoding.UTF8,
                        "application/json")
                };
            });

        using var service = new RobloxAuthService(mockHandler.ToHttpClient());

        var inGameFull = await service.GetUserPresenceAsync("cookie_friend", 156);
        inGameFull.IsInGame.Should().BeTrue();
        inGameFull.PlaceId.Should().Be(2753915549L);
        inGameFull.JobId.Should().Be("11111111-2222-3333-4444-555555555555");
        inGameFull.UniverseId.Should().Be(994732206L);

        var inGameHidden = await service.GetUserPresenceAsync("cookie_friend", 777);
        inGameHidden.IsInGame.Should().BeTrue();
        inGameHidden.PlaceId.Should().BeNull();
        inGameHidden.JobId.Should().BeNull();

        var onlineWebsite = await service.GetUserPresenceAsync("cookie_friend", 888);
        onlineWebsite.IsInGame.Should().BeFalse();
        onlineWebsite.UserPresenceType.Should().Be(1);
    }
}
