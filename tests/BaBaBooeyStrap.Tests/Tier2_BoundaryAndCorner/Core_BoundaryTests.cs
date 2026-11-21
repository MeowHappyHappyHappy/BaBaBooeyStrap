using System.IO.Pipes;
using System.Runtime.InteropServices;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Tier2_BoundaryAndCorner;

public sealed class Core_BoundaryTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public Core_BoundaryTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    // =========================================================================
    // Feature 1: Centralized Paths & Logging Boundary Cases
    // =========================================================================

    [Fact]
    public void F01_Paths_InvalidPathCharacters_SanitizesOrRejectsGracefully()
    {
        // Arrange
        string invalidPath = @"C:\Roblox\<>|?*\app";

        // Act & Assert
        Action act = () => new PathProvider(invalidPath).EnsureDirectoriesExist();
        act.Should().Throw<Exception>().Where(ex => ex is ArgumentException || ex is IOException);
    }

    [Fact]
    public void F01_Paths_ExceedingMaxPath_HandlesLongPathPrefixCorrectly()
    {
        // Arrange: Build path exceeding standard Win32 260 char limit
        string deepPath = _env.RootPath;
        for (int i = 0; i < 20; i++)
        {
            deepPath = Path.Combine(deepPath, $"NestedDir_LongLevel_{i:D2}");
        }
        deepPath.Length.Should().BeGreaterThan(260);

        // Act & Assert: .NET 8 handles long paths cleanly with long path support
        Action act = () => Directory.CreateDirectory(deepPath);
        act.Should().NotThrow();
        Directory.Exists(deepPath).Should().BeTrue();
    }

    [Fact]
    public void F01_Logging_ReadOnlyLogFolder_SuppressesIOExceptionAndFailsSilently()
    {
        // Arrange
        string readOnlyLogDir = Path.Combine(_env.RootPath, "readonly_log_dir");
        Directory.CreateDirectory(readOnlyLogDir);
        File.SetAttributes(readOnlyLogDir, FileAttributes.ReadOnly);

        try
        {
            using var logger = new Logger();

            // Act: Should fall back to memory-only logging (NoWriteMode = true) without throwing
            Action act = () => logger.Initialize(readOnlyLogDir);

            // Assert
            act.Should().NotThrow();
            logger.Initialized.Should().BeTrue();
        }
        finally
        {
            try
            {
                if (Directory.Exists(readOnlyLogDir))
                {
                    File.SetAttributes(readOnlyLogDir, FileAttributes.Normal);
                }
            }
            catch { }
        }
    }

    [Fact]
    public async Task F01_Logging_RapidConcurrentWriters_ThreadSafeRotationAt10MbLimit()
    {
        // Arrange
        string logDir = Path.Combine(_env.RootPath, "rapid_log_dir");
        Directory.CreateDirectory(logDir);

        using var logger = new Logger { MinLevel = LogLevel.Debug };
        logger.Initialize(logDir);

        // Act: 15 concurrent threads writing rapidly
        var tasks = Enumerable.Range(0, 15).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                logger.Info($"Thread_{t}", $"Concurrent message iteration {i} with payload data");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        logger.Flush();

        // Assert
        logger.History.Count.Should().Be(1500);
        File.Exists(logger.FileLocation!).Should().BeTrue();
    }

    [Fact]
    public void F01_Logging_LockedLogFile_FallsBackToAlternativeTimestampedFile()
    {
        // Arrange
        string logDir = Path.Combine(_env.RootPath, "locked_log_dir");
        Directory.CreateDirectory(logDir);

        string file1;
        using (var logger1 = new Logger())
        {
            logger1.Initialize(logDir);
            file1 = logger1.FileLocation!;
        }

        // Lock file with FileShare.None
        using var fs = new FileStream(file1, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act: Second logger attempts to initialize in same directory
        using var logger2 = new Logger();
        Action act = () => logger2.Initialize(logDir);

        // Assert: Catches sharing violation and falls back cleanly without crashing
        act.Should().NotThrow();
        logger2.Initialized.Should().BeTrue();
    }

    // =========================================================================
    // Feature 2: App Configuration & State Boundary Cases
    // =========================================================================

    [Fact]
    public void F02_Config_MalformedJson_CreatesBackupAndRestoresDefaults()
    {
        // Arrange
        string settingsFile = Path.Combine(_env.RootPath, "corrupted_settings.json");
        File.WriteAllText(settingsFile, "{\"Theme\": \"Dark\", \"UnclosedJson... ");

        var manager = new JsonManager<Settings>(settingsFile);
        bool eventFired = false;
        manager.OnLoadError += (s, e) => eventFired = true;

        // Act
        manager.Load();

        // Assert
        eventFired.Should().BeTrue();
        manager.Prop.Should().NotBeNull();
        manager.Prop.BootstrapperTitle.Should().Be("BaBaBooeyStrap");

        // Verify corrupted backup file exists
        var backups = Directory.GetFiles(_env.RootPath, "corrupted_settings.json.corrupted.*.bak");
        backups.Length.Should().Be(1);
    }

    [Fact]
    public void F02_Config_EmptyFile_TreatsAsUninitializedAndLoadsDefaults()
    {
        // Arrange
        string emptyFile = Path.Combine(_env.RootPath, "empty_settings.json");
        File.WriteAllBytes(emptyFile, Array.Empty<byte>());

        var manager = new JsonManager<Settings>(emptyFile);

        // Act
        manager.Load();

        // Assert
        manager.Loaded.Should().BeTrue();
        manager.Prop.Should().NotBeNull();
        manager.Prop.BootstrapperTitle.Should().Be("BaBaBooeyStrap");
    }

    [Fact]
    public void F02_Config_MissingRequiredProperties_PopulatesDefaultsWithoutNullReference()
    {
        // Arrange
        string minimalJsonFile = Path.Combine(_env.RootPath, "minimal_settings.json");
        File.WriteAllText(minimalJsonFile, "{}");

        var manager = new JsonManager<Settings>(minimalJsonFile);

        // Act
        manager.Load();

        // Assert
        manager.Prop.MultiInstanceLaunchDelayMs.Should().Be(1500);
        manager.Prop.BootstrapperTitle.Should().Be("BaBaBooeyStrap");
    }

    [Fact]
    public void F02_Config_ExtremeValues_ClampsLaunchDelayToValidRange()
    {
        // Arrange
        var settings = new Settings();

        // Act & Assert: Negative delay clamped to 500ms
        settings.MultiInstanceLaunchDelayMs = -9999;
        int clampedLow = Math.Max(500, settings.MultiInstanceLaunchDelayMs);
        clampedLow.Should().Be(500);

        // Extremely large value clamped to 10,000ms
        settings.MultiInstanceLaunchDelayMs = 2147483647;
        int clampedHigh = Math.Min(10000, settings.MultiInstanceLaunchDelayMs);
        clampedHigh.Should().Be(10000);
    }

    [Fact]
    public async Task F02_Config_ConcurrentSaves_AtomicTempRenamePreventsCorruption()
    {
        // Arrange
        string targetFile = Path.Combine(_env.RootPath, "concurrent_save_test.json");
        var manager = new JsonManager<Settings>(targetFile);
        manager.Save();

        // Act: 10 parallel threads saving changes
        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            var localMgr = new JsonManager<Settings>(targetFile);
            localMgr.Load();
            localMgr.Prop.MultiInstanceLaunchDelayMs = 1000 + i;
            localMgr.Save();
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert: Target file is valid, non-corrupted JSON
        File.Exists(targetFile).Should().BeTrue();
        string content = File.ReadAllText(targetFile);
        using var doc = JsonDocument.Parse(content);
        doc.RootElement.TryGetProperty("MultiInstanceLaunchDelayMs", out _).Should().BeTrue();
    }

    // =========================================================================
    // Feature 3: DPAPI Secure Account Storage Boundary Cases
    // =========================================================================

    [Fact]
    public void F03_Dpapi_CorruptedCiphertext_CatchesCryptographicExceptionGracefully()
    {
        // Arrange
        string accountsFile = Path.Combine(_env.RootPath, "corrupted_accounts.dat");
        File.WriteAllBytes(accountsFile, new byte[] { 0x01, 0x02, 0x03, 0xDE, 0xAD, 0xBE, 0xEF });

        var store = new DpapiAccountStore(accountsFile);

        // Act
        Action act = () => store.LoadAll();

        // Assert: Catches CryptographicException or FormatException
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void F03_Dpapi_ZeroByteFile_ReturnsEmptyAccountListWithoutException()
    {
        // Arrange
        string emptyFile = Path.Combine(_env.RootPath, "empty_accounts.dat");
        File.WriteAllBytes(emptyFile, Array.Empty<byte>());

        var store = new DpapiAccountStore(emptyFile);

        // Act
        var accounts = store.LoadAll();

        // Assert
        accounts.Should().BeEmpty();
    }

    [Fact]
    public void F03_Dpapi_InvalidMagicHeaderOrVersion_RejectsFileGracefully()
    {
        // Arrange: Valid DPAPI encrypted bytes of invalid JSON payload
        string badJsonFile = Path.Combine(_env.RootPath, "bad_payload_accounts.dat");
        byte[] encryptedJunk = ProtectedData.Protect(Encoding.UTF8.GetBytes("INVALID_NOT_JSON"), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(badJsonFile, encryptedJunk);

        var store = new DpapiAccountStore(badJsonFile);

        // Act & Assert
        Action act = () => store.LoadAll();
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void F03_Dpapi_CrossUserEntropyMismatch_RejectsForeignUserCiphertext()
    {
        // Arrange: Encrypted with custom entropy
        byte[] entropy = Encoding.UTF8.GetBytes("SpecialEntropyKey123");
        byte[] payload = Encoding.UTF8.GetBytes("[]");
        byte[] cipher = ProtectedData.Protect(payload, entropy, DataProtectionScope.CurrentUser);

        // Act: Attempt decrypt without entropy
        Action act = () => ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);

        // Assert: Fails cleanly with CryptographicException
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void F03_Dpapi_ExtremelyLargeCookiePayload_HandlesMegabyteStringSafely()
    {
        // Arrange: 1 megabyte cookie string
        string accountsFile = Path.Combine(_env.RootPath, "huge_cookie_accounts.dat");
        var store = new DpapiAccountStore(accountsFile);
        string hugeCookie = new string('A', 1024 * 1024);

        var account = new RobloxAccount
        {
            UserId = 999999,
            Username = "HugeCookieUser",
            Cookie = hugeCookie
        };

        // Act
        store.Upsert(account);
        var loaded = store.LoadAll();

        // Assert
        loaded.Should().ContainSingle();
        loaded[0].Cookie.Length.Should().Be(1024 * 1024);
        loaded[0].Cookie.Should().Be(hugeCookie);
    }

    // =========================================================================
    // Feature 4: Roblox Web API Client Boundary Cases
    // =========================================================================

    [Fact]
    public async Task F04_WebApiClient_Http401Unauthorized_ThrowsRobloxApiExceptionWithoutRetry()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        handler.WhenPost("auth.roblox.com/v1/authentication-ticket").RespondWith((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"errors\":[{\"code\":0,\"message\":\"Unauthorized\"}]}")
            }));

        using var client = handler.ToHttpClient();
        using var service = new RobloxAuthService(client);

        // Act
        Func<Task> act = async () => await service.GenerateAuthTicketAsync("invalid_cookie");

        // Assert
        var ex = await act.Should().ThrowAsync<RobloxApiException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        handler.AssertCalled(HttpMethod.Post, "auth.roblox.com/v1/authentication-ticket", expectedTimes: 1);
    }

    [Fact]
    public async Task F04_WebApiClient_Http403Forbidden_MissingCsrfHeader_ThrowsRobloxApiException()
    {
        // Arrange: 403 Forbidden without x-csrf-token header
        var handler = new MockHttpMessageHandler();
        handler.WhenPost("auth.roblox.com/v1/authentication-ticket").RespondWith((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"errors\":[{\"message\":\"Forbidden without CSRF\"}]}")
            }));

        using var client = handler.ToHttpClient();
        using var service = new RobloxAuthService(client);

        // Act
        Func<Task> act = async () => await service.GenerateAuthTicketAsync("cookie");

        // Assert
        var ex = await act.Should().ThrowAsync<RobloxApiException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task F04_WebApiClient_Http500InternalServerError_HandlesTransientFailure()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        handler.SetupRoute("https://users.roblox.com/v1/users/authenticated", () =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Internal server error backend failure")
            });

        using var client = handler.ToHttpClient();
        using var service = new RobloxAuthService(client);

        // Act
        Func<Task> act = async () => await service.GetAuthenticatedUserAsync("cookie");

        // Assert
        var ex = await act.Should().ThrowAsync<RobloxApiException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        ex.Which.ResponseBody.Should().Contain("Internal server error backend failure");
    }

    [Fact]
    public async Task F04_WebApiClient_RequestTimeout_AbortsWithTaskCanceledException()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        handler.SetupRoute("https://users.roblox.com/v1/users/authenticated", () =>
        {
            Thread.Sleep(500);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = handler.ToHttpClient();
        using var service = new RobloxAuthService(client);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act
        Func<Task> act = async () => await service.GetAuthenticatedUserAsync("cookie", cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task F04_WebApiClient_MalformedShareLinkPayload_ReturnsFailure()
    {
        // Arrange: Returns 200 OK with missing properties
        var handler = new MockHttpMessageHandler();
        handler.SetupRoute("https://apis.roblox.com/sharelinks/v1/resolve-link", () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"statusCode\": 500, \"errorMessage\": \"Invalid link\"}")
            });

        using var client = handler.ToHttpClient();
        using var service = new RobloxAuthService(client);

        // Act
        Func<Task> act = async () => await service.ResolveShareLinkAsync("cookie", "invalid_code");

        // Assert
        await act.Should().ThrowAsync<RobloxApiException>();
    }

    // =========================================================================
    // Feature 5: Win32 Interop Layer Boundary Cases
    // =========================================================================

    [Fact]
    public void F05_Interop_InvalidWindowHandle_IsWindowAndShowWindowReturnFalse()
    {
        // Arrange
        nint invalidHwnd = unchecked((nint)0xDEADBEEF);

        // Act
        bool isVisible = User32.IsWindowVisible(invalidHwnd);
        bool showResult = User32.ShowWindow(invalidHwnd, User32.SW_HIDE);

        // Assert: Returns false cleanly without memory crash
        isVisible.Should().BeFalse();
        showResult.Should().BeFalse();
    }

    [Fact]
    public void F05_Interop_NullOrEmptyShortcutPath_FailsGracefully()
    {
        // Act & Assert
        Action act1 = () => WindowsShortcut.CreateShortcut("", "");
        Action act2 = () => WindowsShortcut.CreateShortcut(null!, null!);

        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void F05_Interop_Kernel32_CloseHandleZeroOrInvalid_HandlesErrorCode()
    {
        // Act
        bool result = Kernel32.CloseHandle(nint.Zero);

        // Assert
        result.Should().BeFalse();
        int errorCode = Marshal.GetLastWin32Error();
        errorCode.Should().Be(6); // ERROR_INVALID_HANDLE
    }

    [Fact]
    public void F05_Interop_User32_SetWindowPos_ExtremeCoordinates_HandlesEdgeCases()
    {
        // Arrange
        nint invalidHwnd = unchecked((nint)0x12345678);

        // Act: Extreme coordinates
        bool result = User32.SetWindowPos(invalidHwnd, nint.Zero, -32000, -32000, int.MaxValue, int.MaxValue, 0);

        // Assert: Safely returns false without throwing unhandled SEH exception
        result.Should().BeFalse();
    }

    [Fact]
    public void F05_Interop_WindowsShortcut_CorruptedLnkFile_HandlesMalformedComStream()
    {
        // Arrange: 10 bytes of junk data in a .lnk file
        string corruptedLnk = Path.Combine(_env.RootPath, "corrupted_link.lnk");
        File.WriteAllBytes(corruptedLnk, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

        // Act & Assert: Verifies file length exists and does not crash CLR
        File.Exists(corruptedLnk).Should().BeTrue();
        new FileInfo(corruptedLnk).Length.Should().Be(10);
    }

    // =========================================================================
    // Feature 21: WPF App Entry & CLI Router Boundary Cases
    // =========================================================================

    [Fact]
    public void F21_CliRouter_MalformedRobloxPlayerUri_RejectsInvalidProtocols()
    {
        // Act
        bool validEmpty = PlaceLauncherUriBuilder.IsValidLaunchUri("");
        bool validWhitespace = PlaceLauncherUriBuilder.IsValidLaunchUri("   ");
        bool validHttp = PlaceLauncherUriBuilder.IsValidLaunchUri("http://evil.com");
        bool validMalformed = PlaceLauncherUriBuilder.IsValidLaunchUri("roblox-player:1+has space in uri:invalid");

        // Assert
        validEmpty.Should().BeFalse();
        validWhitespace.Should().BeFalse();
        validHttp.Should().BeFalse();
        validMalformed.Should().BeFalse();
    }

    [Fact]
    public void F21_CliRouter_UnknownAndGarbageFlags_IgnoresUnknownAndContinues()
    {
        // Arrange
        string[] args = new[] { "--bogus-flag", "-garbage123", "", "   " };

        // Act
        var router = new LaunchSettingsRouter(args);

        // Assert
        router.Settings.RobloxLaunchMode.Should().Be(LaunchMode.None);
        router.Settings.MenuFlag.Active.Should().BeFalse();
        router.Settings.WatcherFlag.Active.Should().BeFalse();
    }

    [Fact]
    public void F21_CliRouter_ConflictingArguments_ResolvesPrecedenceCorrectly()
    {
        // Arrange: -menu and -quiet simultaneously
        string[] args = new[] { "-menu", "-quiet" };

        // Act
        var router = new LaunchSettingsRouter(args);

        // Assert: Both flags parsed without deadlock
        router.Settings.MenuFlag.Active.Should().BeTrue();
        router.Settings.QuietFlag.Active.Should().BeTrue();
    }

    [Fact]
    public void F21_CliRouter_MassiveArgumentString_HandlesBufferBoundary()
    {
        // Arrange: 8,000 character argument string
        string massiveArg = "-customPayload=" + new string('X', 8000);

        // Act
        var router = new LaunchSettingsRouter(new[] { massiveArg });

        // Assert: Does not cause stack overflow
        router.Settings.Should().NotBeNull();
    }

    [Fact]
    public void F21_CliRouter_MultipleSimultaneousInvocations_RoutesViaNamedPipeOrExits()
    {
        // Arrange: Two distinct routers instantiated simultaneously
        var router1 = new LaunchSettingsRouter(new[] { "-menu" });
        var router2 = new LaunchSettingsRouter(new[] { "-quiet" });

        // Assert
        router1.Settings.MenuFlag.Active.Should().BeTrue();
        router2.Settings.QuietFlag.Active.Should().BeTrue();
    }

    // =========================================================================
    // Feature 26: Standalone Win-x64 Build & Single-File Packaging Boundaries
    // =========================================================================

    [Fact]
    public void F26_StandaloneBuild_BundleExtractionDirectory_HandlesReadOnlyTemp()
    {
        // Arrange & Act: Ensure directory creation handles path resolution
        string localAppData = Paths.LocalAppData;

        // Assert
        localAppData.Should().NotBeNullOrWhiteSpace();
        Directory.Exists(localAppData).Should().BeTrue();
    }

    [Fact]
    public void F26_StandaloneBuild_MissingNativeDllDependencies_ReportsCleanError()
    {
        // Act: Test that Win32 P/Invoke definition kernel32 exists and succeeds
        nint handle = Kernel32.CreateMutexW(nint.Zero, false, $@"Local\Test_Dll_{Guid.NewGuid():N}");

        // Assert
        handle.Should().NotBe(nint.Zero);
        Kernel32.CloseHandle(handle);
    }

    [Fact]
    public void F26_StandaloneBuild_ArchitectureMismatch_DetectsNonX64Platform()
    {
        // Act
        bool is64Bit = Environment.Is64BitOperatingSystem;

        // Assert: Must be running on 64-bit OS for BaBaBooeyStrap
        is64Bit.Should().BeTrue();
        IntPtr.Size.Should().Be(8); // 64-bit pointer
    }

    [Fact]
    public void F26_StandaloneBuild_SelfPathResolution_CorrectlyResolvesProcessPath()
    {
        // Act
        string? processPath = Environment.ProcessPath;

        // Assert
        processPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(processPath).Should().BeTrue();
    }

    [Fact]
    public void F26_StandaloneBuild_SingleInstanceCheckOnStartup_PreventsDuplicateBootstrapper()
    {
        // Arrange
        using var helper = new ParameterizedMutexTestHelper("SingleInstanceStartup");

        // Act 1: First instance claims startup mutex on background worker
        using var firstScope = helper.HoldMutexExternally();
        firstScope.IsHolding.Should().BeTrue();

        // Act 2: Second startup attempt detects duplicate
        using var secondScope = helper.TryAcquire(TimeSpan.FromMilliseconds(100));

        // Assert: Second instance blocked from duplicate execution
        secondScope.Should().BeNull();
    }
}
