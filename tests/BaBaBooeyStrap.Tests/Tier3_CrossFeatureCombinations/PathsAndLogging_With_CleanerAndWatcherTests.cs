using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier3_CrossFeatureCombinations;

public sealed class PathsAndLogging_With_CleanerAndWatcherTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public PathsAndLogging_With_CleanerAndWatcherTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public void T3_PathProvider_Logger_Cleaner_RotatesAndPurgesExpiredLogsWhilePreservingActiveLog()
    {
        var paths = new PathProvider(_env.BaBaBooeyStrapPath);
        paths.EnsureDirectoriesExist();

        string staleLog = Path.Combine(paths.Logs, $"stale_session_{Guid.NewGuid():N}.log");
        string activeLogPath;
        using (var logger = new Logger { MinLevel = LogLevel.Info })
        {
            logger.Initialize(paths.Logs);
            activeLogPath = logger.FileLocation!;
            logger.Info("CleanerIntegration", $"Scanning user path: {Path.Combine(Paths.UserProfile, "secret_file.txt")}");
            logger.Flush();

            // Create stale log after logger.Initialize (which runs its own 7-day cleanup)
            // so we test Cleaner.Clean reclaiming expired logs while active logger file stays intact.
            File.WriteAllText(staleLog, "expired diagnostic log entry");
            File.SetLastWriteTimeUtc(staleLog, DateTime.UtcNow.AddDays(-21));

            var cleanResult = Cleaner.Clean(new[] { paths.Logs }, TimeSpan.FromDays(7), allowedRootPrefix: paths.Base);
            cleanResult.FilesDeleted.Should().BeGreaterThanOrEqualTo(1);

            File.Exists(staleLog).Should().BeFalse();
            File.Exists(activeLogPath).Should().BeTrue();
        }

        using var fs = new FileStream(activeLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string content = reader.ReadToEnd();
        content.Should().Contain("[CleanerIntegration]");
        content.Should().Contain("%USERPROFILE%");
        content.Should().NotContain(Paths.UserProfile);
    }

    [Fact]
    public async Task T3_PathProvider_Logger_ActivityWatcher_RecordsGameEventsInCanonicalLogsDirectory()
    {
        var paths = new PathProvider(_env.BaBaBooeyStrapPath);
        paths.EnsureDirectoriesExist();

        using var logger = new Logger { MinLevel = LogLevel.Info };
        logger.Initialize(paths.Logs);

        using var logStream = _env.CreateActiveLogStream($"t3_path_watcher_{Guid.NewGuid():N}.log");
        using var watcher = new ActivityWatcher();

        var joinTcs = new TaskCompletionSource<GameJoinEventArgs>();
        watcher.GameJoined += (s, e) =>
        {
            logger.Info("ActivityWatcher", $"Joined place {e.PlaceId} job {e.JobId} at {e.ServerIp}");
            logger.Flush();
            joinTcs.TrySetResult(e);
        };

        watcher.StartWatching(logStream.FilePath);
        logStream.WriteLine("2026-11-14T14:30:01.000Z,1.000,0,Info [FLog::Output] ! Joining game 'job-log-pair' place 774411 at 128.116.5.9");

        var completed = await Task.WhenAny(joinTcs.Task, Task.Delay(3000));
        completed.Should().Be(joinTcs.Task);

        logger.History.Should().Contain(line => line.Contains("[ActivityWatcher]") && line.Contains("774411") && line.Contains("job-log-pair"));

        using var fs = new FileStream(logger.FileLocation!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string persistedLog = reader.ReadToEnd();
        persistedLog.Should().Contain("Joined place 774411 job job-log-pair at 128.116.5.9");
    }

    [Fact]
    public void T3_PathProvider_JsonManager_PersistsSettingsAndAppStateAtCanonicalPaths()
    {
        var paths = new PathProvider(_env.BaBaBooeyStrapPath);
        paths.EnsureDirectoriesExist();

        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        settingsMgr.Prop.BootstrapperTitle = "T3CanonicalPathTest";
        settingsMgr.Save();

        var stateMgr = new JsonManager<AppState>(paths.StateFile);
        stateMgr.Prop.VersionGuid = "version-t3-canonical";
        stateMgr.Save();

        var reloadedSettings = new JsonManager<Settings>(paths.SettingsFile);
        reloadedSettings.Load();
        var reloadedState = new JsonManager<AppState>(paths.StateFile);
        reloadedState.Load();

        reloadedSettings.Prop.BootstrapperTitle.Should().Be("T3CanonicalPathTest");
        reloadedState.Prop.VersionGuid.Should().Be("version-t3-canonical");
    }
}
