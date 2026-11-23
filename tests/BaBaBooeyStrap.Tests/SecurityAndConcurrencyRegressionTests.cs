using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Custom;
using BaBaBooeyStrap.Integrations.Watcher;
using BaBaBooeyStrap.Integrations.Window;
using ICSharpCode.SharpZipLib.Zip;

namespace BaBaBooeyStrap.Tests;

public sealed class SecurityAndConcurrencyRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public SecurityAndConcurrencyRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BBBS_SecReg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void ActivityWatcher_RejectsFLogOutputSpoofingInvalidIpsAndUnsafeRpc()
    {
        using var watcher = new ActivityWatcher(options: new ActivityWatcherOptions
        {
            RpcRateLimit = TimeSpan.Zero
        });

        int rpcCount = 0;
        int appClosedCount = 0;
        int disconnectedCount = 0;
        DateTime? serverStartParsed = null;

        watcher.BloxstrapRpcReceived += (_, _) => rpcCount++;
        watcher.AppClosed += (_, _) => appClosedCount++;
        watcher.GameLeft += (_, _) => disconnectedCount++;
        watcher.ServerStartTimeParsed += (_, dt) => serverStartParsed = dt;

        // Spoofed serverId / teleport / close / server prefix under [FLog::Output] must be rejected
        watcher.ProcessLine("[FLog::Output] ! Joining game '11111111-1111-1111-1111-111111111111' place 1818 at 128.116.1.1");
        watcher.ProcessLine("[FLog::Output] doTeleport: joinScriptUrl=\"https://www.roblox.com\" JoinTypeId:6");
        watcher.ProcessLine("[FLog::Output] serverId: 128.116.1.1|53640");
        watcher.InGame.Should().BeFalse();

        watcher.ProcessLine("[FLog::Output] Server Prefix: srv_20260101T000000Z_RCC_1");
        serverStartParsed.Should().BeNull();

        watcher.ProcessLine("[FLog::Output] leaveUGCGameInternal");
        appClosedCount.Should().Be(0);

        // Invalid IP in network line must be rejected
        watcher.ProcessLine("[FLog::Network] serverId: 999.999.1.1|53640");
        watcher.InGame.Should().BeFalse();

        // Legitimate [FLog::Network] serverId joins cleanly, then spoofed disconnect is ignored
        watcher.ProcessLine("[FLog::Network] serverId: 128.116.1.1|53640");
        watcher.InGame.Should().BeTrue();
        watcher.ProcessLine("[FLog::Output] Time to disconnect replication data: 12.3");
        disconnectedCount.Should().Be(0);
        watcher.InGame.Should().BeTrue();

        // Unsafe BloxstrapRPC payloads (>16KB, depth > 8, disallowed command) must be ignored
        string hugeData = new('x', 20_000);
        watcher.ProcessLine($"[FLog::Output] [BloxstrapRPC] {{\"command\":\"SetDetails\",\"data\":\"{hugeData}\"}}");
        watcher.ProcessLine("[FLog::Output] [BloxstrapRPC] {\"command\":\"SetDetails\",\"data\":[[[[[[[[[1]]]]]]]]]}");
        watcher.ProcessLine("[FLog::Output] [BloxstrapRPC] {\"command\":\"RunShellCommand\",\"data\":\"calc\"}");
        rpcCount.Should().Be(0);

        // Valid allowed command still works
        watcher.ProcessLine("[FLog::Output] [BloxstrapRPC] {\"command\":\"SetDetails\",\"data\":\"ok\"}");
        rpcCount.Should().Be(1);
    }

    [Fact]
    public async Task ActivityWatcher_OverlongLineAndThrowingSubscriber_DoNotStopProcessing()
    {
        string logPath = Path.Combine(_tempDir, "watcher_overlong.log");
        string hugeLine = new('A', 70_000);
        File.WriteAllText(
            logPath,
            hugeLine + "\n" +
            "[FLog::Output] ! Joining game '22222222-2222-2222-2222-222222222222' place 2020 at 128.116.2.2\n" +
            "[FLog::Network] serverId: 128.116.2.2|53640\n");

        using var watcher = new ActivityWatcher(options: new ActivityWatcherOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            RpcRateLimit = TimeSpan.Zero
        });

        var joinTcs = new TaskCompletionSource<GameJoinEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.LogLineRead += (_, _) => throw new InvalidOperationException("Subscriber failure");
        watcher.GameJoined += (_, _) => throw new InvalidOperationException("First subscriber failure");
        watcher.GameJoined += (_, e) => joinTcs.TrySetResult(e);

        watcher.StartWatching(logPath);

        var joined = await joinTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        joined.PlaceId.Should().Be("2020");
    }

    [Fact]
    public async Task RunningInstanceTracker_StartDoesNotDeadlockOnSynchronousWinEventAndIsolatesSubscribers()
    {
        var mon = new MonitorInfo(1, @"\\.\DISPLAY1", true,
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 });

        var interop = new ReentrantWinEventMockInterop();
        var observed = new List<ObservedRobloxWindow>
        {
            new(1234, 0x1234, "Roblox", false, true, mon)
        };

        using var tracker = new RunningInstanceTracker(
            () => new Settings(),
            interop,
            new StubMonitorProvider(mon),
            windowProbe: () => observed);

        int secondSubscriberCalls = 0;
        tracker.InstancesChanged += (_, _) => throw new InvalidOperationException("Boom");
        tracker.InstancesChanged += (_, _) => Interlocked.Increment(ref secondSubscriberCalls);

        await Task.Run(() => tracker.Start()).WaitAsync(TimeSpan.FromSeconds(3));
        tracker.Stop();

        secondSubscriberCalls.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void HotkeyParser_SupportsLiteralPlusAndFullRoundTrip()
    {
        HotkeyParser.TryParse("Ctrl++", out var ctrlPlus).Should().BeTrue();
        ctrlPlus.Modifiers.Should().Be(User32.MOD_CONTROL | User32.MOD_NOREPEAT);
        ctrlPlus.VirtualKey.Should().Be(0xBBu);

        HotkeyParser.TryParse("Ctrl+Alt++", out var ctrlAltPlus).Should().BeTrue();
        ctrlAltPlus.Modifiers.Should().Be(User32.MOD_CONTROL | User32.MOD_ALT | User32.MOD_NOREPEAT);
        ctrlAltPlus.VirtualKey.Should().Be(0xBBu);

        HotkeyParser.TryParse("Ctrl+", out _).Should().BeFalse();

        foreach (string combo in new[] { "Ctrl+Escape", "Ctrl+Shift+Add", "Alt+F12" })
        {
            HotkeyParser.TryParse(combo, out var first).Should().BeTrue();
            HotkeyParser.TryParse(first.NormalizedText, out var roundTripped).Should().BeTrue();
            roundTripped.Modifiers.Should().Be(first.Modifiers);
            roundTripped.VirtualKey.Should().Be(first.VirtualKey);
        }
    }

    [Fact]
    public async Task CustomIntegrationRunner_RejectsUnsafePathsUsesDirectExecAndCancelsOnDispose()
    {
        string localExe = Path.Combine(_tempDir, "valid_tool.exe");
        File.WriteAllText(localExe, "stub");

        var captured = new List<System.Diagnostics.ProcessStartInfo>();
        using var runner = new CustomIntegrationRunner(
            processStarter: psi =>
            {
                captured.Add(psi);
                return 4001;
            },
            processTerminator: _ => { },
            delayAsync: (_, _) => Task.CompletedTask);

        var items = new List<CustomIntegration>
        {
            new() { Name = "Rel", Location = "cmd.exe", PreLaunch = true },
            new() { Name = "Unc", Location = @"\\attacker\share\payload.exe", PreLaunch = true },
            new() { Name = "Uri", Location = "https://example.com/payload.exe", PreLaunch = true },
            new() { Name = "Local", Location = localExe, LaunchArgs = "--safe", PreLaunch = true }
        };

        await runner.LaunchIntegrationsAsync(items, preLaunchOnly: true);
        captured.Should().ContainSingle();
        captured[0].FileName.Should().Be(localExe);
        captured[0].UseShellExecute.Should().BeFalse();

        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayRunner = new CustomIntegrationRunner(
            processStarter: _ => 4002,
            delayAsync: async (_, token) =>
            {
                delayStarted.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            });

        var launchTask = delayRunner.LaunchIntegrationsAsync(
            [new CustomIntegration { Name = "Delayed", Location = localExe, Delay = 5000, PreLaunch = true }],
            preLaunchOnly: true);

        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        delayRunner.Dispose();

        Func<Task> awaitLaunch = async () => await launchTask.WaitAsync(TimeSpan.FromSeconds(2));
        await awaitLaunch.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void ProcessLifecycleMonitor_TerminateProcessSafely_FailsClosedOnNullStartTime()
    {
        var monitor = new ProcessLifecycleMonitor();
        monitor.TerminateProcessSafely(Environment.ProcessId, expectedStartTimeUtc: null, TimeSpan.FromMilliseconds(10))
            .Should().BeFalse();
    }

    [Fact]
    public async Task AppUpdateService_RejectsUntrustedHostAndMissingDigest()
    {
        var paths = new PathProvider(_tempDir);
        using var client = new HttpClient(new HttpClientHandler());
        var svc = new AppUpdateService(paths, client);

        var untrustedInfo = new AppUpdateInfo(
            CurrentVersion: new Version(1, 0, 0),
            LatestVersion: new Version(2, 0, 0),
            TagName: "v2.0.0",
            ReleaseNotes: "notes",
            HtmlUrl: "https://github.com/MeowHappyHappyHappy/BaBaBooeyStrap/releases/latest",
            AssetDownloadUrl: "https://untrusted.example.com/BaBaBooeyStrap.exe",
            AssetFileName: "BaBaBooeyStrap.exe",
            AssetSizeBytes: 1024,
            IsUpdateAvailable: true,
            AssetSha256: "sha256:" + new string('a', 64));

        await FluentActions.Invoking(() => svc.DownloadAndScheduleInstallAsync(untrustedInfo))
            .Should().ThrowAsync<InvalidOperationException>();

        var noDigestInfo = untrustedInfo with
        {
            AssetDownloadUrl = "https://github.com/MeowHappyHappyHappy/BaBaBooeyStrap/releases/download/v2.0/BaBaBooeyStrap.exe",
            AssetSha256 = null
        };

        string? result = await svc.DownloadAndScheduleInstallAsync(noDigestInfo);
        result.Should().BeNull();
    }

    [Fact]
    public void DpapiAccountStore_FailClosedOnCorruptStoreAndMetadataExcludesSecret()
    {
        string storePath = Path.Combine(_tempDir, "accounts.dat");
        var store = new DpapiAccountStore(storePath);

        var account = new RobloxAccount
        {
            UserId = 777123,
            Username = "TestUser",
            DisplayName = "Test Display",
            Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.|_SECRET_COOKIE"
        };
        store.Upsert(account);

        var metadata = store.GetMetadataByUserId(777123);
        metadata.Should().NotBeNull();
        metadata!.Username.Should().Be("TestUser");
        metadata.Cookie.Should().BeNullOrEmpty();

        byte[] corruptBytes = Encoding.UTF8.GetBytes("NOT-VALID-DPAPI-BLOB");
        File.WriteAllBytes(storePath, corruptBytes);

        Action upsertCorrupt = () => store.Upsert(new RobloxAccount { UserId = 888, Username = "Other", Cookie = "abc" });
        upsertCorrupt.Should().Throw<InvalidOperationException>();
        File.Exists(storePath).Should().BeFalse();
        var quarantined = Directory.GetFiles(_tempDir, "accounts.dat.corrupted.*.bak");
        quarantined.Should().ContainSingle();
        File.ReadAllBytes(quarantined[0]).Should().Equal(corruptBytes);

        File.WriteAllBytes(storePath, corruptBytes);
        Action deleteCorrupt = () => store.Delete(account.Id);
        deleteCorrupt.Should().Throw<InvalidOperationException>();
        Directory.GetFiles(_tempDir, "accounts.dat.corrupted.*.bak").Should().HaveCount(2);
    }

    [Fact]
    public async Task SingleInstanceIpc_ScopesPipeNameAndSurvivesOversizedPayload()
    {
        using var defaultIpc = new SingleInstanceIpc();
        defaultIpc.PipeName.Should().StartWith($"{SingleInstanceIpc.DefaultPipeName}_");

        string mutexName = $@"Local\BBBS_OversizeMutex_{Guid.NewGuid():N}";
        string pipeName = $"BBBS_OversizePipe_{Guid.NewGuid():N}";

        using var primary = new SingleInstanceIpc(mutexName, pipeName);
        using var secondary = new SingleInstanceIpc(mutexName, pipeName);

        primary.TryAcquirePrimary().Should().BeTrue();
        var receivedTcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ArgumentsReceived += (_, args) => receivedTcs.TrySetResult(args);
        primary.StartPipeServer();

        // Send >64 KB raw payload directly to server pipe
        using (var rawClient = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous))
        {
            await rawClient.ConnectAsync(2000);
            byte[] oversized = new byte[SingleInstanceIpc.MaxPayloadBytes + 1024];
            Array.Fill(oversized, (byte)'A');
            try
            {
                await rawClient.WriteAsync(oversized);
                await rawClient.FlushAsync();
            }
            catch { }
        }

        // Server must stay healthy and process subsequent legitimate message
        await Task.Delay(50);
        bool sentValid = await secondary.SendArgumentsToPrimaryAsync(["-menu"]);
        sentValid.Should().BeTrue(because: secondary.LastIpcError ?? "valid send should succeed");
        (await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3))).Should().Equal("-menu");
    }

    [Fact]
    public void PackageExtractor_RejectsZipBombRatioAndUnixSymlink()
    {
        var extractor = new PackageExtractor();

        // 1. Zip bomb compression ratio (>100x on >1 MB entry)
        byte[] zeros = new byte[2 * 1024 * 1024];
        byte[] bombZip = CreateCustomZip(zip =>
        {
            zip.SetLevel(9);
            var entry = new ZipEntry("bomb.bin") { Size = zeros.Length };
            zip.PutNextEntry(entry);
            zip.Write(zeros, 0, zeros.Length);
            zip.CloseEntry();
        });

        string bombOut = Path.Combine(_tempDir, "bomb_out");
        Action extractBomb = () => extractor.ExtractZip(bombZip, bombOut);
        extractBomb.Should().Throw<InvalidDataException>();

        // 2. Unix symlink entry (0xA000 in upper 16 bits of ExternalFileAttributes, HostSystem = 3)
        byte[] symlinkZip = CreateCustomZip(zip =>
        {
            var entry = new ZipEntry("evil_link")
            {
                HostSystem = 3,
                ExternalFileAttributes = unchecked((int)(0xA1FFu << 16))
            };
            byte[] targetBytes = Encoding.UTF8.GetBytes("/etc/passwd");
            entry.Size = targetBytes.Length;
            zip.PutNextEntry(entry);
            zip.Write(targetBytes, 0, targetBytes.Length);
            zip.CloseEntry();
        });

        string symOut = Path.Combine(_tempDir, "sym_out");
        Action extractSymlink = () => extractor.ExtractZip(symlinkZip, symOut);
        extractSymlink.Should().Throw<SecurityException>();
    }

    [Fact]
    public void GlobalSettingsGuard_RollsBackOnApplyFailureAndQuarantinesCorruptState()
    {
        string xmlPath = Path.Combine(_tempDir, "GlobalBasicSettings_13.xml");
        string originalXml = "<roblox version=\"4\"><Item class=\"GlobalBasicSettings\"><Properties><int name=\"FramerateCap\">60</int></Properties></Item></roblox>";
        File.WriteAllText(xmlPath, originalXml);

        var failingMgr = new ThrowingProfileSettingsManager(xmlPath);
        using (var guard = new GlobalSettingsGuard(failingMgr, _tempDir, () => Array.Empty<int>()))
        {
            guard.EnsureBackupCaptured();
            Action apply = () => guard.ApplyProfileForLaunch(RobloxClientSettingsProfile.CreateAfkLowPerfPreset());
            apply.Should().Throw<IOException>();
        }

        string guardStatePath = Path.Combine(_tempDir, "global_settings_guard.json");
        File.Exists(guardStatePath).Should().BeTrue();
        string persistedJson = File.ReadAllText(guardStatePath);
        persistedJson.Should().Contain("\"ProfileCurrentlyApplied\": false");

        // Now corrupt the guard state file and verify quarantine on startup recovery
        File.WriteAllText(guardStatePath, "{corrupt json!!!");
        var okMgr = new GlobalSettingsManager(xmlPath);
        using (var recoverGuard = new GlobalSettingsGuard(okMgr, _tempDir, () => Array.Empty<int>()))
        {
            Action recover = () => recoverGuard.RecoverOnStartup();
            recover.Should().NotThrow();
        }

        Directory.GetFiles(_tempDir, "global_settings_guard.json.corrupted.*.bak").Should().ContainSingle();
    }

    private static byte[] CreateCustomZip(Action<ZipOutputStream> writeEntries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipOutputStream(ms))
        {
            zip.IsStreamOwner = false;
            writeEntries(zip);
        }
        return ms.ToArray();
    }

    private sealed class StubMonitorProvider(MonitorInfo monitor) : IMonitorProvider
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => [monitor];
        public MonitorInfo GetPrimaryMonitor() => monitor;
        public MonitorInfo GetMonitorFromCursor() => monitor;
        public MonitorInfo GetMonitorFromWindow(nint hWnd) => monitor;
        public MonitorInfo? FindMonitorByDeviceName(string? deviceName) => monitor;
    }

    private sealed class ReentrantWinEventMockInterop : IWin32WindowInterop
    {
        public bool IsWindow(nint hWnd) => true;
        public bool IsWindowVisible(nint hWnd) => true;
        public uint GetWindowThreadProcessId(nint hWnd, out uint processId) { processId = 0; return 1; }
        public string GetClassName(nint hWnd) => "WINDOWSCLIENT";
        public string GetWindowText(nint hWnd) => "Roblox";
        public bool SetWindowText(nint hWnd, string text) => true;
        public nint GetWindowLongPtr(nint hWnd, int nIndex) => nint.Zero;
        public nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong) => nint.Zero;
        public bool GetWindowRect(nint hWnd, out User32.RECT rect) { rect = default; return false; }
        public User32.RECT GetMonitorRectFromWindow(nint hWnd) => new() { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        public int GetSystemMetrics(int nIndex) => 1920;
        public bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags) => true;
        public nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam) => nint.Zero;
        public nint LoadImageIcon(string filePath, int cx, int cy) => nint.Zero;
        public (nint SmallIcon, nint LargeIcon) ExtractFirstIconPair(string executablePath) => (nint.Zero, nint.Zero);
        public bool DestroyIcon(nint hIcon) => true;

        public nint SetWinEventHook(uint eventMin, uint eventMax, User32.WinEventDelegate callback, uint idProcess, uint idThread, uint flags)
        {
            callback(nint.Zero, User32.EVENT_SYSTEM_FOREGROUND, 0x1234, 0, 0, 1, 0);
            return new nint(1);
        }

        public bool UnhookWinEvent(nint hWinEventHook) => true;
        public uint GetCurrentThreadId() => 1;
        public bool PeekMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax, uint removeMsg) { msg = default; return false; }
        public int GetMessage(out User32.MSG msg, nint hWnd, uint filterMin, uint filterMax) { msg = default; return 0; }
        public bool TranslateMessage(ref User32.MSG msg) => false;
        public nint DispatchMessage(ref User32.MSG msg) => nint.Zero;
        public bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam) => true;
    }

    private sealed class ThrowingProfileSettingsManager(string filePath) : IGlobalSettingsManager
    {
        public bool Loaded => true;
        public string FilePath => filePath;
        public bool ReadOnlyLocked { get; set; }
        public int FramerateCap { get; set; } = 60;
        public float MouseSensitivity { get; set; } = 1.0f;
        public int SavedQualityLevel { get; set; } = 1;
        public bool VREnabled { get; set; }
        public float PreferredTransparency { get; set; } = 1.0f;
        public bool ReducedMotion { get; set; }
        public int PreferredTextSize { get; set; } = 1;

        public void Load() { }
        public void Save() { }
        public string? GetValue(string xpath) => null;
        public void SetValue(string xpath, object? value) { }
        public void EnsureTemplateExists(int defaultFps = 60) { }
        public string? CaptureBackup() => File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        public void RestoreBackup(string? backupContent)
        {
            if (backupContent != null) File.WriteAllText(filePath, backupContent);
        }
        public void ApplyProfile(RobloxClientSettingsProfile profile) => throw new IOException("Simulated profile apply failure");
    }
}
