using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier3_CrossFeatureCombinations;

public sealed class Cleaner_With_ActiveSessionTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public Cleaner_With_ActiveSessionTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public void T3_Cleaner_ActiveSession_DeletesExpiredLogs_KeepsActiveLogFileIntact()
    {
        string oldLog = Path.Combine(_env.RobloxLogsPath, $"old_expired_{Guid.NewGuid():N}.log");
        File.WriteAllText(oldLog, "old log content from 14 days ago");
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-14));

        using var activeLogStream = _env.CreateActiveLogStream($"active_session_{Guid.NewGuid():N}.log");
        activeLogStream.WriteLine("Active live session streaming data");

        var result = Cleaner.Clean(new[] { _env.RobloxLogsPath }, TimeSpan.FromDays(7), _env.RootPath);

        File.Exists(oldLog).Should().BeFalse();
        File.Exists(activeLogStream.FilePath).Should().BeTrue();
        result.FilesDeleted.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void T3_Cleaner_ActiveSession_RunningRobloxInstances_SafelySkipsLockedLogFiles()
    {
        string lockedLog = Path.Combine(_env.RobloxLogsPath, $"locked_{Guid.NewGuid():N}.log");
        File.WriteAllText(lockedLog, "locked stale file");
        File.SetLastWriteTimeUtc(lockedLog, DateTime.UtcNow.AddDays(-10));

        using var lockStream = new FileStream(lockedLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = Cleaner.Clean(new[] { _env.RobloxLogsPath }, TimeSpan.FromDays(1), _env.RootPath);

        // Does not throw IOException; skips locked file
        File.Exists(lockedLog).Should().BeTrue();
    }

    [Fact]
    public void T3_Cleaner_ActiveSession_SafeRootValidation_PreventsPathTraversalEscape()
    {
        string outsideDir = Path.Combine(Path.GetTempPath(), $"OutsideTarget_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);

        try
        {
            Action act = () => Cleaner.Clean(new[] { outsideDir }, TimeSpan.FromDays(1), _env.RootPath);
            act.Should().Throw<InvalidOperationException>().WithMessage("*Safety violation*");
        }
        finally
        {
            try { Directory.Delete(outsideDir, true); } catch { }
        }
    }

    [Fact]
    public void T3_Cleaner_ActiveSession_ReclaimsDiskSpace_UpdatesDiagnosticState()
    {
        string downloadsDir = Path.Combine(_env.BaBaBooeyStrapPath, "Downloads");
        Directory.CreateDirectory(downloadsDir);

        byte[] dummyData = new byte[1024];
        for (int i = 0; i < 3; i++)
        {
            string pkgFile = Path.Combine(downloadsDir, $"stale_pkg_{i}_{Guid.NewGuid():N}.zip");
            File.WriteAllBytes(pkgFile, dummyData);
            File.SetLastWriteTimeUtc(pkgFile, DateTime.UtcNow.AddDays(-30));
        }

        var res = Cleaner.Clean(new[] { downloadsDir }, TimeSpan.FromDays(1), _env.RootPath);

        res.FilesDeleted.Should().BeGreaterThanOrEqualTo(3);
        res.BytesReclaimed.Should().BeGreaterThanOrEqualTo(3072);
    }

    [Fact]
    public void T3_Cleaner_ActiveSession_PreemptiveMutexHeld_DoesNotDisruptMultiInstanceController()
    {
        using var helper = new ParameterizedMutexTestHelper("T3_Cleaner_Mutex");
        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        controller.Enable();

        controller.GetStatus().OwnsMutex.Should().BeTrue();

        string tempCleanDir = Path.Combine(_env.BaBaBooeyStrapPath, "TempLogs");
        Directory.CreateDirectory(tempCleanDir);
        string tempFile = Path.Combine(tempCleanDir, "temp_clean.log");
        File.WriteAllText(tempFile, "temp log");
        File.SetLastWriteTimeUtc(tempFile, DateTime.UtcNow.AddDays(-5));

        var cleanResult = Cleaner.Clean(new[] { tempCleanDir }, TimeSpan.FromDays(1), _env.RootPath);

        controller.GetStatus().OwnsMutex.Should().BeTrue();
    }
}
