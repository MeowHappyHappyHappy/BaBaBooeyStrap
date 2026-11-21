using System.Text.RegularExpressions;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.Tests.Core;

public class LoggerAndPathsTests : IDisposable
{
    private readonly string _tempDirectory;

    public LoggerAndPathsTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "BaBaBooeyStrap_LogPathTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
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
    public void Paths_DefaultProperties_AreNotNullOrEmpty()
    {
        var provider = new PathProvider();

        provider.Base.Should().NotBeNullOrWhiteSpace();
        provider.Downloads.Should().NotBeNullOrWhiteSpace();
        provider.Logs.Should().NotBeNullOrWhiteSpace();
        provider.Versions.Should().NotBeNullOrWhiteSpace();
        provider.Modifications.Should().NotBeNullOrWhiteSpace();
        provider.AccountsFile.Should().EndWith("accounts.dat");
        provider.SettingsFile.Should().EndWith("Settings.json");
        provider.Roblox.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Paths_EnsureDirectoriesExist_CreatesExpectedDirectories()
    {
        string customBase = Path.Combine(_tempDirectory, "CustomBase");
        var provider = new PathProvider(customBase);

        provider.EnsureDirectoriesExist();

        Directory.Exists(provider.Base).Should().BeTrue();
        Directory.Exists(provider.Downloads).Should().BeTrue();
        Directory.Exists(provider.Logs).Should().BeTrue();
        Directory.Exists(provider.Versions).Should().BeTrue();
        Directory.Exists(provider.Modifications).Should().BeTrue();
        Directory.Exists(provider.CustomThemes).Should().BeTrue();
        Directory.Exists(provider.SavedFlagProfiles).Should().BeTrue();
        Directory.Exists(provider.Integrations).Should().BeTrue();
    }

    [Fact]
    public void Paths_GetVersionDirectory_And_GetClientSettingsPath()
    {
        var provider = new PathProvider(_tempDirectory);

        string versionDir = provider.GetVersionDirectory("version-test123");
        versionDir.Should().Be(Path.Combine(_tempDirectory, "Versions", "version-test123"));

        string versionSettings = provider.GetClientSettingsPath("version-test123");
        versionSettings.Should().Be(Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json"));

        string modSettings = provider.GetClientSettingsPath();
        modSettings.Should().Be(Path.Combine(_tempDirectory, "Modifications", "ClientSettings", "ClientAppSettings.json"));
    }

    [Fact]
    public void Logger_Formatting_MatchesSpecification()
    {
        string logDir = Path.Combine(_tempDirectory, "Logs");
        using var logger = new Logger { MinLevel = LogLevel.Debug };
        logger.Initialize(customLogDirectory: logDir);

        logger.Info("TestModule::Init", "Starting test logger");
        logger.Flush();

        logger.History.Should().ContainSingle();
        string entry = logger.History[0];

        // Specification: [yyyy-MM-ddTHH:mm:ss.fffZ] [pid:tid] [level,-5] [identifier] message
        var regex = new Regex(@"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\] \[\d+:\d+\] \[INFO \] \[TestModule::Init\] Starting test logger$");
        regex.IsMatch(entry).Should().BeTrue($"Log line '{entry}' should match expected format.");

        // Verify file on disk
        logger.FileLocation.Should().NotBeNull();
        File.Exists(logger.FileLocation!).Should().BeTrue();

        string fileContent;
        using (var fs = new FileStream(logger.FileLocation!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs, Encoding.UTF8))
        {
            fileContent = reader.ReadToEnd();
        }
        fileContent.Should().Contain("[INFO ] [TestModule::Init] Starting test logger");
    }

    [Fact]
    public void Logger_MasksUserProfilePII()
    {
        string logDir = Path.Combine(_tempDirectory, "Logs");
        using var logger = new Logger();
        logger.Initialize(customLogDirectory: logDir);

        string userProfile = Paths.UserProfile;
        string sensitiveMessage = $"File located at {userProfile}\\secret\\path.txt";
        logger.Info("PrivacyCheck", sensitiveMessage);

        logger.History.Should().ContainSingle();
        logger.History[0].Should().NotContain(userProfile);
        logger.History[0].Should().Contain("%USERPROFILE%\\secret\\path.txt");
    }

    [Fact]
    public void Logger_ConcurrentWrites_DoNotThrowOrCorrupt()
    {
        string logDir = Path.Combine(_tempDirectory, "Logs");
        using var logger = new Logger();
        logger.Initialize(customLogDirectory: logDir);

        Parallel.For(0, 50, i =>
        {
            logger.Info("WorkerThread", $"Message number {i}");
        });

        logger.Flush();
        logger.History.Should().HaveCount(50);
    }

    [Fact]
    public void Logger_RetentionCleanup_DeletesOldFiles()
    {
        string logDir = Path.Combine(_tempDirectory, "Logs");
        Directory.CreateDirectory(logDir);

        // Create a simulated 10-day-old log file
        string oldLog = Path.Combine(logDir, "BaBaBooeyStrap_old.log");
        File.WriteAllText(oldLog, "old log content");
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-10));

        // Create a recent 1-day-old log file
        string recentLog = Path.Combine(logDir, "BaBaBooeyStrap_recent.log");
        File.WriteAllText(recentLog, "recent log content");
        File.SetLastWriteTimeUtc(recentLog, DateTime.UtcNow.AddDays(-1));

        using var logger = new Logger();
        logger.Initialize(customLogDirectory: logDir);

        // Old file should have been deleted, recent file preserved
        File.Exists(oldLog).Should().BeFalse();
        File.Exists(recentLog).Should().BeTrue();
    }
}
