using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Bootstrapper_GlobalSettingsGuardTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _xmlPath;
    private readonly GlobalSettingsManager _manager;

    public Bootstrapper_GlobalSettingsGuardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BaBaBooeyGuardTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _xmlPath = Path.Combine(_tempDir, "GlobalBasicSettings_13.xml");
        GlobalSettingsManager.EnsureConfigured(_xmlPath, defaultFps: 240);
        _manager = new GlobalSettingsManager(_xmlPath);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_xmlPath))
            {
                File.SetAttributes(_xmlPath, FileAttributes.Normal);
            }
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task ApplyProfile_RestoreAndProtect_LocksReadOnlyUntilProtectedPidsExit()
    {
        var livePids = new List<int> { 1001, 1002 };
        using var guard = new GlobalSettingsGuard(_manager, _tempDir, () => livePids.ToArray());

        string originalXml = File.ReadAllText(_xmlPath);
        var afkProfile = RobloxClientSettingsProfile.CreateAfkLowPerfPreset();

        await using (await guard.BeginLaunchSessionAsync())
        {
            guard.EnsureBackupCaptured();
            guard.ApplyProfileForLaunch(afkProfile);

            _manager.Load();
            _manager.FramerateCap.Should().Be(30);
            _manager.SavedQualityLevel.Should().Be(1);

            guard.RestoreOriginalAndProtect(livePids, enableReadOnlyProtection: true);
        }

        // Original settings must be restored and protected as ReadOnly while PIDs are alive
        File.ReadAllText(_xmlPath).Should().Be(originalXml);
        _manager.ReadOnlyLocked.Should().BeTrue();
        guard.IsProtectionActive.Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "global_settings_guard.json")).Should().BeTrue();

        // Simulate Roblox attempting to overwrite GlobalBasicSettings_13.xml while protected
        Action overwriteAttempt = () => File.WriteAllText(_xmlPath, "<roblox>overwritten</roblox>");
        overwriteAttempt.Should().Throw<UnauthorizedAccessException>();

        // First PID exits -> still protected because PID 1002 is still running
        livePids.Remove(1001);
        await Task.Delay(120);
        _manager.ReadOnlyLocked.Should().BeTrue();
        guard.ProtectedPids.Should().ContainSingle().Which.Should().Be(1002);

        // Last PID exits -> temporary ReadOnly lock must be lifted and state file cleaned up
        livePids.Remove(1002);
        await WaitUntilAsync(() => !guard.IsProtectionActive && !File.Exists(Path.Combine(_tempDir, "global_settings_guard.json")), TimeSpan.FromSeconds(3));

        _manager.ReadOnlyLocked.Should().BeFalse();
        File.ReadAllText(_xmlPath).Should().Be(originalXml);
    }

    [Fact]
    public async Task PreservesUserOriginalReadOnlyAttribute_AfterProtectedPidsExit()
    {
        File.SetAttributes(_xmlPath, FileAttributes.ReadOnly);
        var livePids = new List<int> { 2001 };
        using var guard = new GlobalSettingsGuard(_manager, _tempDir, () => livePids.ToArray());

        await using (await guard.BeginLaunchSessionAsync())
        {
            guard.EnsureBackupCaptured();
            guard.ApplyProfileForLaunch(RobloxClientSettingsProfile.CreateAfkLowPerfPreset());
            guard.RestoreOriginalAndProtect(livePids, enableReadOnlyProtection: true);
        }

        livePids.Clear();
        await WaitUntilAsync(() => !File.Exists(Path.Combine(_tempDir, "global_settings_guard.json")), TimeSpan.FromSeconds(3));

        // Must preserve the user's pre-existing ReadOnly lock
        _manager.ReadOnlyLocked.Should().BeTrue();
    }

    [Fact]
    public async Task RecoverOnStartup_RestoresInterruptedProfileApplyAndCleansDeadPids()
    {
        string originalXml = File.ReadAllText(_xmlPath);

        // Simulate crash mid-launch where AFK profile was applied and state persisted
        using (var crashedGuard = new GlobalSettingsGuard(_manager, _tempDir, () => [3001]))
        {
            await using (await crashedGuard.BeginLaunchSessionAsync())
            {
                crashedGuard.EnsureBackupCaptured();
                crashedGuard.ApplyProfileForLaunch(RobloxClientSettingsProfile.CreateAfkLowPerfPreset());
            }
        }

        // Confirm file on disk is still dirty from simulated crash
        _manager.Load();
        _manager.FramerateCap.Should().Be(30);

        // New startup with PID 3001 dead recovers original XML and lifts state file
        using var recoveredGuard = new GlobalSettingsGuard(_manager, _tempDir, () => Array.Empty<int>());
        recoveredGuard.RecoverOnStartup();

        File.ReadAllText(_xmlPath).Should().Be(originalXml);
        _manager.ReadOnlyLocked.Should().BeFalse();
        File.Exists(Path.Combine(_tempDir, "global_settings_guard.json")).Should().BeFalse();
    }

    [Fact]
    public async Task GeneralMultiInstanceWithoutOverrides_ProtectsReadOnlyAndSerializesSessions()
    {
        var livePids = new List<int> { 4001, 4002 };
        using var guard = new GlobalSettingsGuard(_manager, _tempDir, () => livePids.ToArray());

        var session1 = await guard.BeginLaunchSessionAsync();
        var session2Task = guard.BeginLaunchSessionAsync();

        // Session 2 must wait while session 1 holds the launch gate
        await Task.Delay(50);
        session2Task.IsCompleted.Should().BeFalse();

        guard.EnsureBackupCaptured();
        guard.RestoreOriginalAndProtect(livePids, enableReadOnlyProtection: true);
        await session1.DisposeAsync();

        var session2 = await session2Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session2.DisposeAsync();

        _manager.ReadOnlyLocked.Should().BeTrue();
        guard.IsProtectionActive.Should().BeTrue();

        livePids.Clear();
        await WaitUntilAsync(() => !guard.IsProtectionActive, TimeSpan.FromSeconds(3));
        _manager.ReadOnlyLocked.Should().BeFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        condition().Should().BeTrue();
    }
}
