using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.ViewModels.Sections;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using FluentAssertions;

namespace BaBaBooeyStrap.Tests.App;

public sealed class AccountAvatarTests : IDisposable
{
    private readonly string _tempDir;

    public AccountAvatarTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BBBS_Avatar_Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task GetAvatarThumbnailUrlsAsync_ParsesBatchedResponseAndSkipsInvalidEntries()
    {
        string? requestedUri = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestedUri = request.RequestUri?.ToString();
            const string json = """
                {
                  "data": [
                    { "targetId": 1001, "state": "Completed", "imageUrl": "https://tr.rbxcdn.com/1001.png" },
                    { "targetId": 1002, "state": "Blocked", "imageUrl": "" },
                    { "targetId": 0, "state": "Completed", "imageUrl": "https://tr.rbxcdn.com/0.png" },
                    { "targetId": 1003, "state": "Completed", "imageUrl": "https://tr.rbxcdn.com/1003.png" }
                  ]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        using var auth = new RobloxAuthService(httpClient);

        var result = await auth.GetAvatarThumbnailUrlsAsync([1001, 0, -5, 1002, 1001, 1003]);

        requestedUri.Should().NotBeNull();
        requestedUri.Should().Contain("userIds=1001,1002,1003");
        requestedUri.Should().Contain("isCircular=true");
        result.Should().HaveCount(2);
        result[1001].Should().Be("https://tr.rbxcdn.com/1001.png");
        result[1003].Should().Be("https://tr.rbxcdn.com/1003.png");
        result.ContainsKey(1002).Should().BeFalse();
    }

    [Fact]
    public async Task GetAvatarThumbnailUrlsAsync_EmptyOrNonPositiveInput_ReturnsEmptyWithoutHttpCall()
    {
        int callCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var httpClient = new HttpClient(handler);
        using var auth = new RobloxAuthService(httpClient);

        var result = await auth.GetAvatarThumbnailUrlsAsync([0, -1]);

        result.Should().BeEmpty();
        callCount.Should().Be(0);
    }

    [Fact]
    public void AccountItem_AvatarUrlAndInitial_NormalizeAndNotify()
    {
        var account = new RobloxAccount { Username = "  alice ", UserId = 42 };
        var item = new AccountItem(account);

        item.AvatarInitial.Should().Be("A");
        item.AvatarUrl.Should().BeNull();

        var changedProperties = new List<string?>();
        item.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        item.AvatarUrl = "  https://tr.rbxcdn.com/alice.png  ";
        item.AvatarUrl.Should().Be("https://tr.rbxcdn.com/alice.png");
        changedProperties.Should().Contain(nameof(AccountItem.AvatarUrl));

        item.AvatarUrl = "   ";
        item.AvatarUrl.Should().BeNull();

        var blankUserItem = new AccountItem(new RobloxAccount { Username = "", UserId = 99 });
        blankUserItem.AvatarInitial.Should().Be("?");
    }

    [Fact]
    public async Task AccountsViewModel_ReloadAccounts_FetchesAndCachesAccountAvatars()
    {
        var paths = new PathProvider(_tempDir);
        var accountStore = new DpapiAccountStore(paths.AccountsFile);
        accountStore.Upsert(new RobloxAccount { Username = "PlayerOne", DisplayName = "PlayerOne", UserId = 111, Cookie = "c1" });
        accountStore.Upsert(new RobloxAccount { Username = "PlayerTwo", DisplayName = "PlayerTwo", UserId = 222, Cookie = "c2" });

        var auth = new CountingAvatarAuthService();
        using var vm = new AccountsViewModel(
            new Settings(),
            paths,
            accountStore,
            new JoinTargetStore(paths.TargetsFile),
            multiInstanceController: null,
            processSupervisor: null,
            launcher: null,
            new GlobalSettingsManager(paths.RobloxGlobalSettings),
            stateManager: null,
            setLocalizedStatus: (_, _) => { },
            authService: auth);

        for (int i = 0; i < 40 && vm.AccountItems.Any(a => a.AvatarUrl == null); i++)
        {
            await Task.Delay(25);
        }

        auth.BatchCallCount.Should().Be(1);
        vm.AccountItems.First(a => a.UserId == 111).AvatarUrl.Should().Be("https://tr.rbxcdn.com/111.png");
        vm.AccountItems.First(a => a.UserId == 222).AvatarUrl.Should().Be("https://tr.rbxcdn.com/222.png");

        // Second reload should apply cached avatar URLs synchronously without another batch fetch
        vm.ReloadAccounts();
        await Task.Delay(50);

        auth.BatchCallCount.Should().Be(1);
        vm.AccountItems.First(a => a.UserId == 111).AvatarUrl.Should().Be("https://tr.rbxcdn.com/111.png");
        vm.AccountItems.First(a => a.UserId == 222).AvatarUrl.Should().Be("https://tr.rbxcdn.com/222.png");
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class CountingAvatarAuthService : IRobloxAuthService
    {
        public int BatchCallCount { get; private set; }

        public Task<AuthenticatedUser> GetAuthenticatedUserAsync(string cookie, CancellationToken ct = default) =>
            Task.FromResult(new AuthenticatedUser(1, "Stub", "Stub"));

        public Task<string> GenerateAuthTicketAsync(string cookie, CancellationToken ct = default) =>
            Task.FromResult("TICKET");

        public Task<ResolvedShareLink> ResolveShareLinkAsync(string cookie, string shareCode, CancellationToken ct = default) =>
            Task.FromResult(new ResolvedShareLink(1, "CODE"));

        public Task<string?> GetAvatarThumbnailUrlAsync(long userId, string size = "150x150", bool isCircular = true, CancellationToken ct = default) =>
            Task.FromResult<string?>($"https://tr.rbxcdn.com/{userId}.png");

        public Task<IReadOnlyDictionary<long, string>> GetAvatarThumbnailUrlsAsync(
            IEnumerable<long> userIds,
            string size = "150x150",
            bool isCircular = true,
            CancellationToken ct = default)
        {
            BatchCallCount++;
            IReadOnlyDictionary<long, string> dict = userIds
                .Where(static id => id > 0)
                .Distinct()
                .ToDictionary(id => id, id => $"https://tr.rbxcdn.com/{id}.png");
            return Task.FromResult(dict);
        }

        public Task<long> ResolveUserIdByUsernameAsync(string username, CancellationToken ct = default) =>
            Task.FromResult(1L);

        public Task<RobloxUserPresence> GetUserPresenceAsync(string cookie, long userId, CancellationToken ct = default) =>
            Task.FromResult(new RobloxUserPresence(userId, 0, null, null, null, null, null));

        public Task<RobloxGameInfo?> GetGameInfoByPlaceIdAsync(long placeId, CancellationToken ct = default) =>
            Task.FromResult<RobloxGameInfo?>(null);

        public void Dispose() { }
    }
}
