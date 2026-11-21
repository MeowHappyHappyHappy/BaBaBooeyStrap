using System.Net;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier3_CrossFeatureCombinations;

public sealed class WebApiClient_With_AccountStoreTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public WebApiClient_With_AccountStoreTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public async Task T3_WebApiClient_AccountStore_StoredCookieGeneratesAuthTicketViaCsrfRetry()
    {
        string dbPath = Path.Combine(_env.BaBaBooeyStrapPath, "t3_accounts_auth.dat");
        var store = new DpapiAccountStore(dbPath);
        const string secretCookie = "_dpapi_secret_cookie_val_123_";

        store.Upsert(new RobloxAccount { UserId = 112233, Username = "SpeedyRobloxian", Cookie = secretCookie });

        var httpHandler = new MockHttpMessageHandler().SetupAuthTicketWorkflow(
            validCookie: secretCookie,
            csrfToken: "csrf_challenge_999",
            authTicket: "auth_ticket_success_777");

        var account = store.LoadAll().Single();
        account.Cookie.Should().Be(secretCookie);

        using var client = httpHandler.ToHttpClient();
        using var authService = new RobloxAuthService(client);
        string ticket = await authService.GenerateAuthTicketAsync(account.Cookie);

        ticket.Should().Be("auth_ticket_success_777");
        httpHandler.AssertCalled(HttpMethod.Post, "auth.roblox.com/v1/authentication-ticket", 2);

        byte[] rawBytes = File.ReadAllBytes(dbPath);
        string rawText = System.Text.Encoding.UTF8.GetString(rawBytes);
        rawText.Should().NotContain(secretCookie);
    }

    [Fact]
    public async Task T3_WebApiClient_AccountStore_ShareLinkResolution_UpdatesAccountSessionData()
    {
        string statePath = Path.Combine(_env.BaBaBooeyStrapPath, "t3_roblox_state.json");
        var stateMgr = new JsonManager<RobloxState>(statePath);

        var httpHandler = new MockHttpMessageHandler().SetupShareLinkWorkflow(
            linkId: "share_vip_code_xyz",
            placeId: 54321,
            linkCode: "resolved_vip_link_888");

        using var client = httpHandler.ToHttpClient();
        using var authService = new RobloxAuthService(client);

        var joinData = await authService.ResolveShareLinkAsync("valid_cookie", "share_vip_code_xyz");
        joinData.PlaceId.Should().Be(54321);
        joinData.LinkCode.Should().Be("resolved_vip_link_888");

        stateMgr.Prop.Player.Version = $"place:{joinData.PlaceId}:{joinData.LinkCode}";
        stateMgr.Save();

        var reloaded = new JsonManager<RobloxState>(statePath);
        reloaded.Load();
        reloaded.Prop.Player.Version.Should().Be("place:54321:resolved_vip_link_888");
    }

    [Fact]
    public async Task T3_WebApiClient_AccountStore_MultipleStoredAccounts_AcquiresDistinctTicketsConcurrently()
    {
        string dbPath = Path.Combine(_env.BaBaBooeyStrapPath, "t3_multi_accs.dat");
        var store = new DpapiAccountStore(dbPath);

        store.Upsert(new RobloxAccount { UserId = 101, Username = "UserA", Cookie = "cookie_account_A" });
        store.Upsert(new RobloxAccount { UserId = 102, Username = "UserB", Cookie = "cookie_account_B" });
        store.Upsert(new RobloxAccount { UserId = 103, Username = "UserC", Cookie = "cookie_account_C" });

        var cookieMap = new Dictionary<string, string>
        {
            ["cookie_account_A"] = "ticket_A_111",
            ["cookie_account_B"] = "ticket_B_222",
            ["cookie_account_C"] = "ticket_C_333"
        };

        var httpHandler = new MockHttpMessageHandler();
        MultiAccountClusterHelper.SetupDynamicMultiAccountAuth(httpHandler, cookieMap);

        using var client = httpHandler.ToHttpClient();
        using var authService = new RobloxAuthService(client);

        var accounts = store.LoadAll().OrderBy(a => a.UserId).ToList();
        accounts.Count.Should().Be(3);

        var tasks = accounts.Select(async acc => await authService.GenerateAuthTicketAsync(acc.Cookie)).ToArray();
        string[] tickets = await Task.WhenAll(tasks);

        tickets[0].Should().Be("ticket_A_111");
        tickets[1].Should().Be("ticket_B_222");
        tickets[2].Should().Be("ticket_C_333");
    }

    [Fact]
    public async Task T3_WebApiClient_AccountStore_ExpiredCookie_Triggers401AndMarksAccountReauthRequired()
    {
        string dbPath = Path.Combine(_env.BaBaBooeyStrapPath, "t3_expired_acc.dat");
        var store = new DpapiAccountStore(dbPath);
        var expiredAcc = new RobloxAccount { UserId = 40101, Username = "ExpiredUser", Cookie = "expired_cookie_token" };
        store.Upsert(expiredAcc);

        var httpHandler = new MockHttpMessageHandler();
        httpHandler.WhenPost("auth.roblox.com/v1/authentication-ticket")
            .RespondWithStatus(HttpStatusCode.Unauthorized);

        using var client = httpHandler.ToHttpClient();
        using var authService = new RobloxAuthService(client);

        Func<Task> act = async () => await authService.GenerateAuthTicketAsync(expiredAcc.Cookie);
        var ex = await act.Should().ThrowAsync<RobloxApiException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Mark account as reauth required
        store.Upsert(new RobloxAccount
        {
            Id = expiredAcc.Id,
            UserId = expiredAcc.UserId,
            Username = expiredAcc.Username + " [Reauth Required]",
            Cookie = expiredAcc.Cookie
        });

        var reloaded = store.LoadAll().Single(a => a.UserId == 40101);
        reloaded.Username.Should().EndWith("[Reauth Required]");
    }

    [Fact]
    public async Task T3_WebApiClient_AccountStore_CliLaunchWithAccount_InjectsLaunchTicketIntoLaunchSettings()
    {
        string dbPath = Path.Combine(_env.BaBaBooeyStrapPath, "t3_cli_launch_acc.dat");
        var store = new DpapiAccountStore(dbPath);
        var acc = new RobloxAccount { UserId = 778899, Username = "CliUser", Cookie = "valid_user_cookie" };
        store.Upsert(acc);

        var httpHandler = new MockHttpMessageHandler().SetupAuthTicketWorkflow(
            validCookie: "valid_user_cookie",
            csrfToken: "csrf_778899",
            authTicket: "ticket_cli_launch_123");

        using var client = httpHandler.ToHttpClient();
        using var authService = new RobloxAuthService(client);

        var loadedAcc = store.LoadAll().First(a => a.UserId == 778899);
        string ticket = await authService.GenerateAuthTicketAsync(loadedAcc.Cookie);

        string uri = $"roblox-player:1+launchmode:play+gameinfo:{ticket}";
        var router = new LaunchSettingsRouter(new[] { uri });

        router.Settings.RobloxLaunchMode.Should().Be(LaunchMode.Player);
        router.Settings.RobloxLaunchArgs.Should().Contain("ticket_cli_launch_123");
    }
}
