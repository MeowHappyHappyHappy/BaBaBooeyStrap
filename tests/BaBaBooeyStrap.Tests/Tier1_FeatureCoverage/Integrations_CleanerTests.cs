using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Integrations_CleanerTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public Integrations_CleanerTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public void F19_Cleaner_TwoWeeksPolicy_DeletesFilesOlderThan14Days()
    {
        // Arrange
        string logDir = Path.Combine(_env.RootPath, "cleaner_twoweeks_logs");
        Directory.CreateDirectory(logDir);

        string oldFile = Path.Combine(logDir, "old_log.log");
        File.WriteAllText(oldFile, "Old content");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-20));

        string recentFile = Path.Combine(logDir, "recent_log.log");
        File.WriteAllText(recentFile, "Recent content");
        File.SetLastWriteTimeUtc(recentFile, DateTime.UtcNow.AddDays(-5));

        // Act
        var result = Cleaner.Clean(new[] { logDir }, TimeSpan.FromDays(14), allowedRootPrefix: _env.RootPath);

        // Assert
        result.FilesDeleted.Should().Be(1);
        File.Exists(oldFile).Should().BeFalse();
        File.Exists(recentFile).Should().BeTrue();
    }

    [Fact]
    public void F19_Cleaner_DirectoryTargeting_CleansOnlyConfiguredDirectories()
    {
        // Arrange
        string cleanDir = Path.Combine(_env.RootPath, "clean_target_dir");
        string preserveDir = Path.Combine(_env.RootPath, "preserve_target_dir");
        Directory.CreateDirectory(cleanDir);
        Directory.CreateDirectory(preserveDir);

        string oldFile1 = Path.Combine(cleanDir, "old1.log");
        File.WriteAllText(oldFile1, "Data");
        File.SetLastWriteTimeUtc(oldFile1, DateTime.UtcNow.AddDays(-30));

        string oldFile2 = Path.Combine(preserveDir, "old2.log");
        File.WriteAllText(oldFile2, "Data");
        File.SetLastWriteTimeUtc(oldFile2, DateTime.UtcNow.AddDays(-30));

        // Act: Only target cleanDir
        var result = Cleaner.Clean(new[] { cleanDir }, TimeSpan.FromDays(14), allowedRootPrefix: _env.RootPath);

        // Assert
        result.FilesDeleted.Should().Be(1);
        File.Exists(oldFile1).Should().BeFalse();
        File.Exists(oldFile2).Should().BeTrue();
    }

    [Fact]
    public void F19_Cleaner_SafePathValidation_RejectsPathsOutsideDesignatedFolders()
    {
        // Arrange
        string traversalPath = Path.Combine(Environment.SystemDirectory);

        // Act
        bool isSafe = Cleaner.ValidateDirectorySafety(traversalPath, safeRoot: _env.RootPath);
        Action act = () => Cleaner.Clean(new[] { traversalPath }, TimeSpan.FromDays(14), allowedRootPrefix: _env.RootPath);

        // Assert
        isSafe.Should().BeFalse();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Safety violation*");
    }

    [Fact]
    public void F19_Cleaner_LockedFileResilience_SkipsOpenFileWithoutFailing()
    {
        // Arrange
        string logDir = Path.Combine(_env.RootPath, "cleaner_locked_test");
        Directory.CreateDirectory(logDir);

        string lockedFile = Path.Combine(logDir, "locked.log");
        File.WriteAllText(lockedFile, "Locked content");
        File.SetLastWriteTimeUtc(lockedFile, DateTime.UtcNow.AddDays(-30));

        string normalFile = Path.Combine(logDir, "normal.log");
        File.WriteAllText(normalFile, "Normal content");
        File.SetLastWriteTimeUtc(normalFile, DateTime.UtcNow.AddDays(-30));

        // Lock one file exclusively
        using var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act: Cleaner runs and does not crash
        var result = Cleaner.Clean(new[] { logDir }, TimeSpan.FromDays(14), allowedRootPrefix: _env.RootPath);

        // Assert: Normal file deleted, locked file skipped
        File.Exists(normalFile).Should().BeFalse();
        File.Exists(lockedFile).Should().BeTrue();
        result.FilesDeleted.Should().Be(1);
    }

    [Fact]
    public void F19_Cleaner_ReportDeletedCountAndBytes_ReturnsAccurateSummary()
    {
        // Arrange
        string logDir = Path.Combine(_env.RootPath, "cleaner_summary_test");
        Directory.CreateDirectory(logDir);

        byte[] payload1 = new byte[1000];
        byte[] payload2 = new byte[2500];

        string file1 = Path.Combine(logDir, "f1.log");
        string file2 = Path.Combine(logDir, "f2.log");
        File.WriteAllBytes(file1, payload1);
        File.WriteAllBytes(file2, payload2);
        File.SetLastWriteTimeUtc(file1, DateTime.UtcNow.AddDays(-20));
        File.SetLastWriteTimeUtc(file2, DateTime.UtcNow.AddDays(-20));

        // Act
        var result = Cleaner.Clean(new[] { logDir }, TimeSpan.FromDays(14), allowedRootPrefix: _env.RootPath);

        // Assert
        result.FilesDeleted.Should().Be(2);
        result.BytesReclaimed.Should().Be(3500);
    }
}
