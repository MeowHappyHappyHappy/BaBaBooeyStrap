using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Core_DpapiAndAccountStoreTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public Core_DpapiAndAccountStoreTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public void F3_DpapiAccountStore_EncryptAndDecrypt_RoundTripsRoblosecurityCookie()
    {
        // Arrange
        string accountsFile = Path.Combine(_env.BaBaBooeyStrapPath, "test_dpapi_roundtrip.dat");
        var store = new DpapiAccountStore(accountsFile);
        const string secretCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you|_1234567890abcdef";

        var account = new RobloxAccount
        {
            Id = Guid.NewGuid(),
            UserId = 12345678,
            Username = "TestPlayer1",
            DisplayName = "TestDisplayName",
            Cookie = secretCookie
        };

        // Act
        store.Upsert(account);

        // Verify disk contents are encrypted (not plain text)
        byte[] rawBytes = File.ReadAllBytes(accountsFile);
        string rawText = Encoding.UTF8.GetString(rawBytes);
        rawText.Should().NotContain(secretCookie);

        // Read back via DPAPI store
        var reloadedStore = new DpapiAccountStore(accountsFile);
        var loadedAccounts = reloadedStore.LoadAll();

        // Assert
        loadedAccounts.Should().ContainSingle();
        loadedAccounts[0].Cookie.Should().Be(secretCookie);
        loadedAccounts[0].Username.Should().Be("TestPlayer1");
    }

    [Fact]
    public void F3_DpapiAccountStore_AddMultipleAccounts_StoresIndependently()
    {
        // Arrange
        string accountsFile = Path.Combine(_env.BaBaBooeyStrapPath, "test_dpapi_multiple.dat");
        var store = new DpapiAccountStore(accountsFile);

        var acc1 = new RobloxAccount { UserId = 101, Username = "User101", Cookie = "cookie101" };
        var acc2 = new RobloxAccount { UserId = 102, Username = "User102", Cookie = "cookie102" };
        var acc3 = new RobloxAccount { UserId = 103, Username = "User103", Cookie = "cookie103" };

        // Act
        store.Upsert(acc1);
        store.Upsert(acc2);
        store.Upsert(acc3);

        var all = store.LoadAll();

        // Assert
        all.Count.Should().Be(3);
        all.Select(a => a.UserId).Should().BeEquivalentTo(new[] { 101L, 102L, 103L });
    }

    [Fact]
    public void F3_DpapiAccountStore_UpdateAccount_ModifiesExistingRecordWithoutDuplication()
    {
        // Arrange
        string accountsFile = Path.Combine(_env.BaBaBooeyStrapPath, "test_dpapi_update.dat");
        var store = new DpapiAccountStore(accountsFile);

        var acc = new RobloxAccount
        {
            Id = Guid.NewGuid(),
            UserId = 201,
            Username = "OriginalUser",
            DisplayName = "OriginalDisplay",
            Cookie = "cookie_original"
        };
        store.Upsert(acc);

        // Act: Update display name and cookie
        acc.DisplayName = "UpdatedDisplay";
        acc.Cookie = "cookie_updated";
        store.Upsert(acc);

        var all = store.LoadAll();

        // Assert
        all.Count.Should().Be(1);
        all[0].Id.Should().Be(acc.Id);
        all[0].DisplayName.Should().Be("UpdatedDisplay");
        all[0].Cookie.Should().Be("cookie_updated");
    }

    [Fact]
    public void F3_DpapiAccountStore_RemoveAccount_DeletesTargetAndPreservesOthers()
    {
        // Arrange
        string accountsFile = Path.Combine(_env.BaBaBooeyStrapPath, "test_dpapi_remove.dat");
        var store = new DpapiAccountStore(accountsFile);

        var acc1 = new RobloxAccount { UserId = 301, Username = "KeepMe", Cookie = "keep_cookie" };
        var acc2 = new RobloxAccount { UserId = 302, Username = "DeleteMe", Cookie = "del_cookie" };
        store.Upsert(acc1);
        store.Upsert(acc2);

        // Act
        bool deleted = store.Delete(acc2.Id);

        // Assert
        deleted.Should().BeTrue();
        var reloaded = new DpapiAccountStore(accountsFile).LoadAll();
        reloaded.Should().ContainSingle();
        reloaded[0].UserId.Should().Be(301);
        reloaded[0].Username.Should().Be("KeepMe");
    }

    [Fact]
    public void F3_DpapiAccountStore_GetAll_ReturnsAllStoredAccountsWithDecryptedTokens()
    {
        // Arrange
        string accountsFile = Path.Combine(_env.BaBaBooeyStrapPath, "test_dpapi_getall.dat");
        var store = new DpapiAccountStore(accountsFile);

        var list = new List<RobloxAccount>();
        for (int i = 0; i < 5; i++)
        {
            list.Add(new RobloxAccount
            {
                UserId = 400 + i,
                Username = $"Account_{i}",
                Cookie = $"secret_token_{i}"
            });
        }
        store.SaveAll(list);

        // Act
        var loaded = store.LoadAll();

        // Assert
        loaded.Count.Should().Be(5);
        for (int i = 0; i < 5; i++)
        {
            var acc = loaded.FirstOrDefault(a => a.UserId == 400 + i);
            acc.Should().NotBeNull();
            acc!.Cookie.Should().Be($"secret_token_{i}");
        }
    }
}
