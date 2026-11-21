using System.Diagnostics;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Integrations.Cleaner;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Integrations;

public sealed class CleanerTests : IDisposable
{
    private readonly MockRobloxEnvironment _env = new();

    private TestSandboxPaths CreateSandboxPaths() => new(_env);

    [Fact]
    public void ExecuteCleanup_AgeThresholdAndSelectiveFlags_DeletesOnlyTargetedExpiredFiles()
    {
        var paths = CreateSandboxPaths();
        Directory.CreateDirectory(Path.Combine(paths.RobloxLogs, "crashes"));
        Directory.CreateDirectory(Path.Combine(paths.Roblox, "http"));

        DateTime now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var cleaner = new CacheCleaner(paths, utcNowProvider: () => now);

        string oldLog = Path.Combine(paths.RobloxLogs, "old.log");
        string youngLog = Path.Combine(paths.RobloxLogs, "young.log");
        string oldDump = Path.Combine(paths.RobloxLogs, "crashes", "crash.dmp");
        string oldLauncherLog = Path.Combine(paths.Logs, "launcher_old.log");
        string oldHttpCache = Path.Combine(paths.Roblox, "http", "cache_item.bin");

        File.WriteAllText(oldLog, "old-log-data");
        File.WriteAllText(youngLog, "young-log-data");
        File.WriteAllText(oldDump, "dump-data");
        File.WriteAllText(oldLauncherLog, "launcher-data");
        File.WriteAllText(oldHttpCache, "http-cache-data");

        File.SetLastWriteTimeUtc(oldLog, now.AddDays(-10));
        File.SetLastWriteTimeUtc(youngLog, now.AddDays(-1));
        File.SetLastWriteTimeUtc(oldDump, now.AddDays(-10));
        File.SetLastWriteTimeUtc(oldLauncherLog, now.AddDays(-10));
        File.SetLastWriteTimeUtc(oldHttpCache, now.AddDays(-10));

        // CleanLogs = true, CleanCrashDumps = false, CleanLauncherLogs = false, CleanHttpCache = true
        var report = cleaner.ExecuteCleanup(new CleanerPolicy(
            LogAgeThreshold: TimeSpan.FromDays(7),
            MaxTotalSizeBytes: 0,
            CleanLogs: true,
            CleanCrashDumps: false,
            CleanLauncherLogs: false,
            CleanHttpCache: true));

        report.Errors.Should().BeEmpty();
        report.FilesDeleted.Should().Be(2);
        report.BytesFreed.Should().Be("old-log-data".Length + "http-cache-data".Length);

        File.Exists(oldLog).Should().BeFalse();
        File.Exists(oldHttpCache).Should().BeFalse();
        File.Exists(youngLog).Should().BeTrue();
        File.Exists(oldDump).Should().BeTrue();
        File.Exists(oldLauncherLog).Should().BeTrue();
    }

    [Fact]
    public void ExecuteCleanup_MaxTotalSizeBytes_DeletesOldestFilesFirstUntilUnderCap()
    {
        var paths = CreateSandboxPaths();
        DateTime now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var cleaner = new CacheCleaner(paths, utcNowProvider: () => now);

        // Create 5 files of 1000 bytes each, all younger than age threshold (1h..5h old)
        for (int i = 1; i <= 5; i++)
        {
            string file = Path.Combine(paths.RobloxLogs, $"file_{i}.log");
            File.WriteAllBytes(file, new byte[1000]);
            File.SetLastWriteTimeUtc(file, now.AddHours(-6 + i)); // file_1 is oldest (-5h), file_5 is newest (-1h)
        }

        var report = cleaner.ExecuteCleanup(new CleanerPolicy(
            LogAgeThreshold: TimeSpan.FromDays(30),
            MaxTotalSizeBytes: 2500,
            CleanLogs: true,
            CleanCrashDumps: false,
            CleanLauncherLogs: false,
            CleanHttpCache: false));

        report.FilesDeleted.Should().Be(3);
        report.BytesFreed.Should().Be(3000);

        File.Exists(Path.Combine(paths.RobloxLogs, "file_1.log")).Should().BeFalse();
        File.Exists(Path.Combine(paths.RobloxLogs, "file_2.log")).Should().BeFalse();
        File.Exists(Path.Combine(paths.RobloxLogs, "file_3.log")).Should().BeFalse();
        File.Exists(Path.Combine(paths.RobloxLogs, "file_4.log")).Should().BeTrue();
        File.Exists(Path.Combine(paths.RobloxLogs, "file_5.log")).Should().BeTrue();
    }

    [Fact]
    public void PathSafetyValidator_RejectsPrefixCollisionTraversalAndSystemRoots()
    {
        string safeRoot = _env.RobloxPath;
        string siblingPrefixAttack = _env.RobloxPath + "Evil\\logs";
        string traversalAttack = Path.Combine(_env.RobloxLogsPath, "..", "..", "Outside");

        PathSafetyValidator.ValidateDirectorySafety(_env.RobloxLogsPath, safeRoot).Should().BeTrue();
        PathSafetyValidator.ValidateDirectorySafety(siblingPrefixAttack, safeRoot).Should().BeFalse();
        PathSafetyValidator.ValidateDirectorySafety(traversalAttack, safeRoot).Should().BeFalse();
        PathSafetyValidator.ValidateDirectorySafety(Environment.GetFolderPath(Environment.SpecialFolder.Windows), safeRoot).Should().BeFalse();
        PathSafetyValidator.ValidateDirectorySafety(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), safeRoot).Should().BeFalse();
        PathSafetyValidator.ValidateDirectorySafety(Path.GetTempPath(), Path.GetTempPath(), allowExactMatch: true).Should().BeFalse();

        var paths = CreateSandboxPaths();
        var cleaner = new CacheCleaner(paths);
        Action act = () => cleaner.CleanDirectories(new[] { siblingPrefixAttack }, safeRoot, TimeSpan.FromDays(1));
        act.Should().Throw<InvalidOperationException>().WithMessage("*Safety violation*");
    }

    [Fact]
    public void ExecuteCleanup_ReparsePointJunction_DoesNotTraverseIntoExternalDirectory()
    {
        var paths = CreateSandboxPaths();
        DateTime now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        string externalDir = Path.Combine(_env.RootPath, "ExternalProtected");
        Directory.CreateDirectory(externalDir);
        string externalFile = Path.Combine(externalDir, "secret.log");
        File.WriteAllText(externalFile, "do-not-delete");
        File.SetLastWriteTimeUtc(externalFile, now.AddDays(-60));

        string junctionPath = Path.Combine(paths.RobloxLogs, "escaped_junction");
        bool junctionCreated = TryCreateNtfsJunction(junctionPath, externalDir);

        var cleaner = new CacheCleaner(paths, utcNowProvider: () => now);
        var report = cleaner.ExecuteCleanup(new CleanerPolicy(
            TimeSpan.FromDays(7), 0, CleanLogs: true, CleanCrashDumps: true, CleanLauncherLogs: false, CleanHttpCache: false));

        report.Errors.Should().BeEmpty();
        if (junctionCreated)
        {
            File.Exists(externalFile).Should().BeTrue();
        }
    }

    [Fact]
    public void ExecuteCleanup_LockedAndReadOnlyFiles_SkipsLockedAndDeletesReadOnly()
    {
        var paths = CreateSandboxPaths();
        DateTime now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var cleaner = new CacheCleaner(paths, utcNowProvider: () => now);

        string lockedFile = Path.Combine(paths.RobloxLogs, "locked.log");
        string readOnlyFile = Path.Combine(paths.RobloxLogs, "readonly.log");

        File.WriteAllText(lockedFile, "locked-bytes");
        File.WriteAllText(readOnlyFile, "ro-bytes");
        File.SetLastWriteTimeUtc(lockedFile, now.AddDays(-20));
        File.SetLastWriteTimeUtc(readOnlyFile, now.AddDays(-20));
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);

        using var lockStream = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var report = cleaner.ExecuteCleanup(new CleanerPolicy(
            TimeSpan.FromDays(7), 0, CleanLogs: true, CleanCrashDumps: false, CleanLauncherLogs: false, CleanHttpCache: false));

        File.Exists(lockedFile).Should().BeTrue();
        File.Exists(readOnlyFile).Should().BeFalse();
        report.FilesDeleted.Should().Be(1);
        report.BytesFreed.Should().Be("ro-bytes".Length);
        report.Errors.Should().ContainSingle(e => e.Contains("locked.log"));

        // Locked ReadOnly file must preserve FileAttributes.ReadOnly when deletion fails
        string lockedReadOnlyFile = Path.Combine(paths.RobloxLogs, "locked_ro.log");
        File.WriteAllText(lockedReadOnlyFile, "locked-ro");
        File.SetLastWriteTimeUtc(lockedReadOnlyFile, now.AddDays(-20));
        File.SetAttributes(lockedReadOnlyFile, FileAttributes.ReadOnly);

        using (var roLockStream = new FileStream(lockedReadOnlyFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var roReport = cleaner.ExecuteCleanup(new CleanerPolicy(
                TimeSpan.FromDays(7), 0, CleanLogs: true, CleanCrashDumps: false, CleanLauncherLogs: false, CleanHttpCache: false));
            roReport.Errors.Should().Contain(e => e.Contains("locked_ro.log"));
        }

        File.Exists(lockedReadOnlyFile).Should().BeTrue();
        (File.GetAttributes(lockedReadOnlyFile) & FileAttributes.ReadOnly).Should().Be(FileAttributes.ReadOnly);
    }

    [Fact]
    public void CleanerPolicyMapper_DecouplesCleanCrashDumpsFromCleanLogs()
    {
        var logsOnly = new Settings
        {
            CleanerOptions = CleanerOptions.OneWeek,
            CleanerDirectories = new List<string> { "RobloxLogs" }
        };
        var policyLogsOnly = CleanerPolicyMapper.FromSettings(logsOnly);
        policyLogsOnly.CleanLogs.Should().BeTrue();
        policyLogsOnly.CleanCrashDumps.Should().BeFalse();

        var withCrashDumps = new Settings
        {
            CleanerOptions = CleanerOptions.OneWeek,
            CleanerDirectories = new List<string> { "RobloxLogs", "RobloxCrashDumps" }
        };
        var policyWithDumps = CleanerPolicyMapper.FromSettings(withCrashDumps);
        policyWithDumps.CleanLogs.Should().BeTrue();
        policyWithDumps.CleanCrashDumps.Should().BeTrue();
    }

    private static bool TryCreateNtfsJunction(string linkPath, string targetPath)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _env.Dispose();
    }

    private sealed class TestSandboxPaths : IPaths
    {
        private readonly MockRobloxEnvironment _env;

        public TestSandboxPaths(MockRobloxEnvironment env)
        {
            _env = env;
            Directory.CreateDirectory(TempLogs);
            Directory.CreateDirectory(RobloxCache);
        }

        public string Base => _env.BaBaBooeyStrapPath;
        public string Downloads => Path.Combine(Base, "Downloads");
        public string Logs => Path.Combine(Base, "Logs");
        public string Versions => Path.Combine(Base, "Versions");
        public string Modifications => _env.ModificationsPath;
        public string CustomThemes => Path.Combine(Base, "CustomThemes");
        public string SavedFlagProfiles => Path.Combine(Base, "SavedFlagProfiles");
        public string Integrations => Path.Combine(Base, "Integrations");
        public string Application => Path.Combine(Base, "BaBaBooeyStrap.exe");
        public string AccountsFile => Path.Combine(Base, "accounts.dat");
        public string TargetsFile => Path.Combine(Base, "targets.json");
        public string SettingsFile => Path.Combine(Base, "Settings.json");
        public string StateFile => Path.Combine(Base, "State.json");
        public string RobloxStateFile => Path.Combine(Base, "RobloxState.json");
        public string Temp => Path.Combine(_env.RootPath, "Temp");
        public string TempUpdates => Path.Combine(Temp, "BaBaBooeyStrap", "Updates");
        public string TempLogs => Path.Combine(Temp, "BaBaBooeyStrap", "Logs");
        public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public string WindowsStartMenu => string.Empty;
        public string System => Environment.GetFolderPath(Environment.SpecialFolder.System);
        public string Process => string.Empty;
        public string Roblox => _env.RobloxPath;
        public string RobloxStudio => Path.Combine(_env.RootPath, "RobloxStudio");
        public string RobloxLogs => _env.RobloxLogsPath;
        public string RobloxVersions => _env.RobloxVersionsPath;
        public string RobloxGlobalSettings => _env.GlobalSettingsXmlPath;
        public string RobloxLocalStorage => Path.Combine(Roblox, "LocalStorage");
        public string RobloxCache => Path.Combine(Temp, "Roblox");
        public bool Initialized => true;

        public void Initialize(string? baseDirectory = null) { }
        public void EnsureDirectoriesExist() { }
        public string GetVersionDirectory(string versionGuid) => Path.Combine(Versions, versionGuid);
        public string GetClientSettingsPath(string? versionGuid = null) => Path.Combine(Modifications, "ClientSettings", "ClientAppSettings.json");
    }
}
