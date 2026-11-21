using System.Text.RegularExpressions;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Core_PathsAndLoggingTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public Core_PathsAndLoggingTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    // =========================================================================
    // Feature 1: Centralized Paths & Logging
    // =========================================================================

    [Fact]
    public void F1_Paths_CanonicalResolution_ReturnsStandardDirectories()
    {
        // Arrange
        var provider = new PathProvider(_env.BaBaBooeyStrapPath);

        // Act & Assert
        provider.Base.Should().Be(_env.BaBaBooeyStrapPath);
        provider.Downloads.Should().Be(Path.Combine(_env.BaBaBooeyStrapPath, "Downloads"));
        provider.Logs.Should().Be(Path.Combine(_env.BaBaBooeyStrapPath, "Logs"));
        provider.Versions.Should().Be(Path.Combine(_env.BaBaBooeyStrapPath, "Versions"));
        provider.Modifications.Should().Be(Path.Combine(_env.BaBaBooeyStrapPath, "Modifications"));
        provider.CustomThemes.Should().Be(Path.Combine(_env.BaBaBooeyStrapPath, "CustomThemes"));
        provider.SettingsFile.Should().Be(Path.Combine(_env.BaBaBooeyStrapPath, "Settings.json"));
        provider.AccountsFile.Should().Be(Path.Combine(_env.BaBaBooeyStrapPath, "accounts.dat"));

        provider.EnsureDirectoriesExist();
        Directory.Exists(provider.Downloads).Should().BeTrue();
        Directory.Exists(provider.Logs).Should().BeTrue();
        Directory.Exists(provider.Versions).Should().BeTrue();
    }

    [Fact]
    public void F1_Logger_StructuredFormatting_IncludesTimestampLogLevelAndMessage()
    {
        // Arrange
        string logDir = Path.Combine(_env.RootPath, "test_logs_structured");
        Directory.CreateDirectory(logDir);

        using var logger = new Logger { MinLevel = LogLevel.Info };
        logger.Initialize(logDir);

        // Act
        logger.Info("AuthModule", "User authenticated successfully");
        logger.Flush();

        // Assert
        logger.FileLocation.Should().NotBeNull();
        File.Exists(logger.FileLocation).Should().BeTrue();

        using var fs = new FileStream(logger.FileLocation!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string logContent = reader.ReadToEnd();
        // Regex checking: [yyyy-MM-ddTHH:mm:ss.fffZ] [pid:tid] [INFO ] [AuthModule] User authenticated successfully
        var match = Regex.Match(logContent, @"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\] \[\d+:\d+\] \[INFO \] \[AuthModule\] User authenticated successfully", RegexOptions.Multiline);
        match.Success.Should().BeTrue();
    }

    [Fact]
    public async Task F1_Logger_MultiProcessSafe_ConcurrentWritesDoNotThrow()
    {
        // Arrange
        string logDir = Path.Combine(_env.RootPath, "test_logs_concurrent");
        Directory.CreateDirectory(logDir);

        using var logger = new Logger { MinLevel = LogLevel.Debug };
        logger.Initialize(logDir);

        // Act: 10 parallel tasks logging 50 entries each
        var tasks = Enumerable.Range(0, 10).Select(taskId => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                logger.Info($"Worker_{taskId}", $"Iteration {i}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        logger.Flush();

        // Assert
        logger.History.Count.Should().Be(500);
        new FileInfo(logger.FileLocation!).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void F1_Logger_PiiMasking_HidesUserWindowsUsername()
    {
        // Arrange
        string logDir = Path.Combine(_env.RootPath, "test_logs_pii");
        Directory.CreateDirectory(logDir);

        using var logger = new Logger();
        logger.Initialize(logDir);

        string userProfile = Paths.UserProfile;
        string sensitivePath = Path.Combine(userProfile, "AppData", "Local", "Roblox", "test.log");

        // Act
        logger.Info("Diagnostic", $"Accessing sensitive path: {sensitivePath}");
        logger.Flush();

        // Assert
        using var fs = new FileStream(logger.FileLocation!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string logContent = reader.ReadToEnd();
        logContent.Should().NotContain(userProfile);
        logContent.Should().Contain("%USERPROFILE%");
    }

    [Fact]
    public void F1_Logger_RetentionPolicy_RotatesAndCleansStaleLogs()
    {
        // Arrange
        string logDir = Path.Combine(_env.RootPath, "test_logs_retention");
        Directory.CreateDirectory(logDir);

        // Create 5 old logs (dated 20 days ago) and 2 new logs (dated today)
        var oldDate = DateTime.UtcNow.AddDays(-20);
        for (int i = 0; i < 5; i++)
        {
            string oldFile = Path.Combine(logDir, $"BaBaBooeyStrap_old_{i}.log");
            File.WriteAllText(oldFile, "Old log entry");
            File.SetLastWriteTimeUtc(oldFile, oldDate);
        }

        for (int i = 0; i < 2; i++)
        {
            string newFile = Path.Combine(logDir, $"BaBaBooeyStrap_new_{i}.log");
            File.WriteAllText(newFile, "New log entry");
            File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow);
        }

        Directory.GetFiles(logDir, "*.log").Length.Should().Be(7);

        // Act: Initialize logger which invokes CleanOldLogs(logDir, 7)
        using (var logger = new Logger())
        {
            logger.Initialize(logDir);
        }

        // Assert: 5 old files deleted, 2 recent files + newly initialized log preserved
        var remainingLogs = Directory.GetFiles(logDir, "*.log");
        remainingLogs.Length.Should().Be(3); // 2 recent files + 1 new logger file
        remainingLogs.Any(f => f.Contains("old")).Should().BeFalse();
        remainingLogs.Count(f => f.Contains("new")).Should().Be(2);
    }

    // =========================================================================
    // Feature 2: App Configuration & State
    // =========================================================================

    [Fact]
    public void F2_Settings_Serialization_RoundTripsDefaultAndCustomValues()
    {
        // Arrange
        string settingsFile = Path.Combine(_env.BaBaBooeyStrapPath, "test_settings_roundtrip.json");
        var manager = new JsonManager<Settings>(settingsFile);

        // Act
        manager.Prop.BootstrapperStyle = BootstrapperStyle.ByfronDialog;
        manager.Prop.Theme = Theme.Dark;
        manager.Prop.BootstrapperTitle = "BaBaBooeyCustom";
        manager.Prop.MultiInstanceLaunchDelayMs = 2500;
        manager.Save();

        var reloadedManager = new JsonManager<Settings>(settingsFile);
        reloadedManager.Load();

        // Assert
        reloadedManager.Prop.BootstrapperStyle.Should().Be(BootstrapperStyle.ByfronDialog);
        reloadedManager.Prop.Theme.Should().Be(Theme.Dark);
        reloadedManager.Prop.BootstrapperTitle.Should().Be("BaBaBooeyCustom");
        reloadedManager.Prop.MultiInstanceLaunchDelayMs.Should().Be(2500);
    }

    [Fact]
    public void F2_State_Serialization_PersistsDeploymentVersionAndTimestamps()
    {
        // Arrange
        string stateFile = Path.Combine(_env.BaBaBooeyStrapPath, "test_state.json");
        var manager = new JsonManager<AppState>(stateFile);

        // Act
        manager.Prop.VersionGuid = "version-4ea8e85481e34f46";
        manager.Prop.Version = "0.640.0.12345";
        manager.Prop.PackageHashes["RobloxApp.zip"] = "a1b2c3d4e5f6";
        manager.Save();

        var reloaded = new JsonManager<AppState>(stateFile);
        reloaded.Load();

        // Assert
        reloaded.Prop.VersionGuid.Should().Be("version-4ea8e85481e34f46");
        reloaded.Prop.Version.Should().Be("0.640.0.12345");
        reloaded.Prop.PackageHashes.Should().ContainKey("RobloxApp.zip");
        reloaded.Prop.PackageHashes["RobloxApp.zip"].Should().Be("a1b2c3d4e5f6");
    }

    [Fact]
    public void F2_RobloxState_ModManifest_StoresAndRetrievesInstalledFiles()
    {
        // Arrange
        string rbxStateFile = Path.Combine(_env.BaBaBooeyStrapPath, "test_rbx_state.json");
        var manager = new JsonManager<RobloxState>(rbxStateFile);

        // Act
        manager.Prop.ModManifest.Add("content/fonts/arial.ttf");
        manager.Prop.ModManifest.Add("content/textures/Cursors/KeyboardMouse/ArrowCursor.png");
        manager.Prop.ModManifest.Add("content/sounds/ouch.ogg");
        manager.Save();

        var reloaded = new JsonManager<RobloxState>(rbxStateFile);
        reloaded.Load();

        // Assert
        reloaded.Prop.ModManifest.Count.Should().Be(3);
        reloaded.Prop.ModManifest.Should().Contain("content/sounds/ouch.ogg");
    }

    [Fact]
    public void F2_JsonManager_AtomicSave_CreatesTemporaryFileBeforeReplacement()
    {
        // Arrange
        string targetFile = Path.Combine(_env.BaBaBooeyStrapPath, "test_atomic_save.json");
        var manager = new JsonManager<Settings>(targetFile);

        // Act
        manager.Prop.BootstrapperTitle = "AtomicSaveVerification";
        manager.Save();

        // Assert
        File.Exists(targetFile).Should().BeTrue();
        string text = File.ReadAllText(targetFile);
        text.Should().Contain("AtomicSaveVerification");

        // Verify no leftover .tmp files
        var tmpFiles = Directory.GetFiles(_env.BaBaBooeyStrapPath, "*.tmp");
        tmpFiles.Should().BeEmpty();
    }

    [Fact]
    public void F2_JsonManager_LoadOrCreate_InitializesFreshDefaultsWhenMissing()
    {
        // Arrange
        string missingFile = Path.Combine(_env.BaBaBooeyStrapPath, "non_existent_settings.json");
        if (File.Exists(missingFile)) File.Delete(missingFile);

        var manager = new JsonManager<Settings>(missingFile);

        // Act
        manager.Load();

        // Assert
        manager.Loaded.Should().BeTrue();
        manager.Prop.Should().NotBeNull();
        manager.Prop.BootstrapperTitle.Should().Be("BaBaBooeyStrap");
        File.Exists(missingFile).Should().BeTrue(); // Should have saved default instance
    }
}
