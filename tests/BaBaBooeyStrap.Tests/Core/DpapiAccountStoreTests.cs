using System.Security.Cryptography;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;

namespace BaBaBooeyStrap.Tests.Core;

public class DpapiAccountStoreTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _storeFile;

    public DpapiAccountStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "BaBaBooeyStrap_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _storeFile = Path.Combine(_tempDirectory, "accounts.dat");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void LoadAll_WhenFileDoesNotExist_ReturnsEmptyList()
    {
        var store = new DpapiAccountStore(_storeFile);
        var accounts = store.LoadAll();
        accounts.Should().BeEmpty();
    }

    [Fact]
    public void LoadAll_WhenFileIsEmpty_ReturnsEmptyList()
    {
        File.WriteAllBytes(_storeFile, []);
        var store = new DpapiAccountStore(_storeFile);
        var accounts = store.LoadAll();
        accounts.Should().BeEmpty();
    }

    [Fact]
    public void SaveAll_And_LoadAll_RoundTripsEncryptedAccounts()
    {
        var store = new DpapiAccountStore(_storeFile);

        var account1 = new RobloxAccount
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer1",
            DisplayName = "Player One",
            UserId = 111111,
            Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--111111",
            Notes = "Main account",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            LastLaunchedAt = DateTimeOffset.UtcNow,
            RobloxSettings = new RobloxClientSettingsProfile
            {
                FramerateCap = 144,
                QualityLevel = 10,
                Fullscreen = true,
                CustomOverrides = [new CustomSettingOverride { Key = "GraphicsQuality", Value = "10" }]
            }
        };

        var account2 = new RobloxAccount
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer2",
            DisplayName = "Player Two",
            UserId = 222222,
            Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--222222",
            Notes = "Alt account"
        };

        store.SaveAll([account1, account2]);

        // Verify the file was written
        File.Exists(_storeFile).Should().BeTrue();

        // Verify raw bytes are DPAPI encrypted and do NOT contain plaintext cookies or usernames
        var rawBytes = File.ReadAllBytes(_storeFile);
        rawBytes.Should().NotBeEmpty();
        string rawString = Encoding.UTF8.GetString(rawBytes);
        rawString.Should().NotContain("TestPlayer1");
        rawString.Should().NotContain("_|WARNING:-DO-NOT-SHARE-THIS.--111111");

        // Verify LoadAll successfully decrypts and returns identical data
        var reloadedStore = new DpapiAccountStore(_storeFile);
        var loaded = reloadedStore.LoadAll();
        loaded.Should().HaveCount(2);

        var loaded1 = loaded.First(a => a.Id == account1.Id);
        loaded1.Username.Should().Be("TestPlayer1");
        loaded1.DisplayName.Should().Be("Player One");
        loaded1.UserId.Should().Be(111111);
        loaded1.Cookie.Should().Be("_|WARNING:-DO-NOT-SHARE-THIS.--111111");
        loaded1.Notes.Should().Be("Main account");
        loaded1.RobloxSettings.Should().NotBeNull();
        loaded1.RobloxSettings!.FramerateCap.Should().Be(144);
        loaded1.RobloxSettings.QualityLevel.Should().Be(10);
        loaded1.RobloxSettings.Fullscreen.Should().BeTrue();
        loaded1.RobloxSettings.CustomOverrides.Should().ContainSingle(o => o.Key == "GraphicsQuality" && o.Value == "10");

        var loaded2 = loaded.First(a => a.Id == account2.Id);
        loaded2.Username.Should().Be("TestPlayer2");
        loaded2.UserId.Should().Be(222222);
    }

    [Fact]
    public void Upsert_NewAndExisting_WorksCorrectly()
    {
        var store = new DpapiAccountStore(_storeFile);

        var account = new RobloxAccount
        {
            Id = Guid.NewGuid(),
            Username = "OriginalUser",
            UserId = 12345,
            Cookie = "test_cookie"
        };

        // Insert new
        store.Upsert(account);
        store.LoadAll().Should().HaveCount(1);
        store.GetById(account.Id).Should().NotBeNull();
        store.GetById(account.Id)!.Username.Should().Be("OriginalUser");

        // Update in-place
        account.Username = "UpdatedUser";
        account.Notes = "Updated notes";
        store.Upsert(account);

        var all = store.LoadAll();
        all.Should().HaveCount(1);
        var updated = store.GetById(account.Id);
        updated.Should().NotBeNull();
        updated!.Username.Should().Be("UpdatedUser");
        updated.Notes.Should().Be("Updated notes");
    }

    [Fact]
    public void Delete_RemovesAccountSuccessfully()
    {
        var store = new DpapiAccountStore(_storeFile);
        var account1 = new RobloxAccount { Id = Guid.NewGuid(), Username = "User1" };
        var account2 = new RobloxAccount { Id = Guid.NewGuid(), Username = "User2" };

        store.SaveAll([account1, account2]);
        store.LoadAll().Should().HaveCount(2);

        // Delete existing
        bool deleted = store.Delete(account1.Id);
        deleted.Should().BeTrue();
        store.LoadAll().Should().HaveCount(1);
        store.GetById(account1.Id).Should().BeNull();
        store.GetById(account2.Id).Should().NotBeNull();

        // Delete non-existent
        bool deleteNonExistent = store.Delete(Guid.NewGuid());
        deleteNonExistent.Should().BeFalse();
        store.LoadAll().Should().HaveCount(1);
    }

    [Fact]
    public void LoadAll_CorruptedBytes_ThrowsCryptographicException()
    {
        // Write random corrupted bytes that cannot be DPAPI-decrypted
        byte[] garbage = new byte[64];
        Random.Shared.NextBytes(garbage);
        File.WriteAllBytes(_storeFile, garbage);

        var store = new DpapiAccountStore(_storeFile);
        var act = () => store.LoadAll();
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void SaveAll_AtomicWrite_LeavesNoTempFiles()
    {
        var store = new DpapiAccountStore(_storeFile);
        for (int i = 0; i < 5; i++)
        {
            store.SaveAll([new RobloxAccount { Username = $"User_{i}" }]);
        }

        var tempFiles = Directory.GetFiles(_tempDirectory, "*.tmp");
        tempFiles.Should().BeEmpty();
    }

    [Fact]
    public void MultiInstance_ConcurrentAccess_DoesNotThrowSharingViolations()
    {
        string tempFile = Path.Combine(_tempDirectory, $"concurrent_multi_{Guid.NewGuid():N}.dat");
        try
        {
            Parallel.For(0, 10, threadId =>
            {
                for (int i = 0; i < 15; i++)
                {
                    var store = new DpapiAccountStore(tempFile);
                    var account = new RobloxAccount
                    {
                        Id = Guid.NewGuid(),
                        Username = $"User_{threadId}_{i}",
                        UserId = (long)threadId * 1000 + i,
                        Cookie = $"cookie_{threadId}_{i}"
                    };

                    store.Upsert(account);

                    var reloaded = store.LoadAll();
                    reloaded.Should().NotBeNull();
                    var fetched = store.GetById(account.Id);
                    fetched.Should().NotBeNull();
                    fetched!.Username.Should().Be(account.Username);
                }
            });

            var finalStore = new DpapiAccountStore(tempFile);
            var finalAccounts = finalStore.LoadAll();
            finalAccounts.Should().HaveCount(150);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    [Fact]
    public void MultiInstance_ConcurrentUpserts_DoNotThrowIOException()
    {
        string tempFile = Path.Combine(_tempDirectory, $"acc_stress_{Guid.NewGuid():N}.dat");
        try
        {
            Parallel.For(0, 10, threadId =>
            {
                for (int i = 0; i < 15; i++)
                {
                    var store = new DpapiAccountStore(tempFile);
                    store.Upsert(new RobloxAccount
                    {
                        Id = Guid.NewGuid(),
                        Username = $"User_{threadId}_{i}"
                    });
                }
            });

            var store = new DpapiAccountStore(tempFile);
            store.LoadAll().Should().HaveCount(150);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }
}
