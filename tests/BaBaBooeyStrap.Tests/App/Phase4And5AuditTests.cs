using System.Text.RegularExpressions;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.Bootstrapper.Mods.Presets;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Integrations.Watcher;

namespace BaBaBooeyStrap.Tests.App;

[Collection("LocalizationSingleton")]
public sealed class Phase4And5AuditTests : IDisposable
{
    private readonly string _tempDir;

    public Phase4And5AuditTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BBBS_Audit45_{Guid.NewGuid():N}");
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
        catch
        {
        }
    }

    [Theory]
    [InlineData("FFlagDebugDisplayFPS", FastFlagType.Boolean)]
    [InlineData("DFFlagTaskScheduler", FastFlagType.Boolean)]
    [InlineData("SFFlagSomething", FastFlagType.Boolean)]
    [InlineData("FIntTargetFps", FastFlagType.Integer)]
    [InlineData("DFIntTaskSchedulerTargetFps", FastFlagType.Integer)]
    [InlineData("SFIntSomething", FastFlagType.Integer)]
    [InlineData("FStringDebug", FastFlagType.String)]
    [InlineData("DFStringCrashUpload", FastFlagType.String)]
    [InlineData("SFStringExperiment", FastFlagType.String)]
    [InlineData("FFLogLegacy", FastFlagType.Log)]
    [InlineData("FLogNetwork", FastFlagType.Log)]
    [InlineData("DFLogHttpTrace", FastFlagType.Log)]
    [InlineData("SFLogAudio", FastFlagType.Log)]
    [InlineData("CustomOther", FastFlagType.Unknown)]
    public void FastFlag_InferType_RecognizesAllRobloxPrefixes(string name, FastFlagType expected)
    {
        FastFlag.InferType(name).Should().Be(expected);
    }

    [Fact]
    public void FastFlagEditorViewModel_ImportJson_SupportsMergeAndReplaceModes()
    {
        var vm = new FastFlagEditorViewModel();
        vm.AddFlag("FFlagExisting", "False");
        vm.AddFlag("FIntKeepMe", "10");

        bool merged = vm.ImportJson("""{"fflagexisting": true, "DFIntAdded": 42}""", replaceExisting: false);
        merged.Should().BeTrue();
        vm.TotalFlagCount.Should().Be(3);
        vm.Flags.Should().Contain(f => f.Name == "FIntKeepMe" && f.Value == "10");
        vm.Flags.Should().Contain(f => f.Name.Equals("FFlagExisting", StringComparison.OrdinalIgnoreCase) && f.Value == "True");
        vm.Flags.Should().Contain(f => f.Name == "DFIntAdded" && f.Value == "42");

        bool replaced = vm.ImportJson("""{"FStringOnly": "abc"}""", replaceExisting: true);
        replaced.Should().BeTrue();
        vm.TotalFlagCount.Should().Be(1);
        vm.Flags.Single().Name.Should().Be("FStringOnly");
    }

    [Fact]
    public void MainWindowViewModel_ClampWindowBounds_ClampsToSmallAndNegativeVirtualDesktops()
    {
        var oversized = MainWindowViewModel.ClampWindowBounds(
            new BaBaBooeyStrap.Core.Models.WindowState { Width = 1400, Height = 900, Left = 750, Top = 550 },
            virtualLeft: 0,
            virtualTop: 0,
            virtualWidth: 800,
            virtualHeight: 600);

        oversized.Width.Should().Be(800);
        oversized.Height.Should().Be(600);
        oversized.Left.Should().Be(0);
        oversized.Top.Should().Be(0);

        var tiny = MainWindowViewModel.ClampWindowBounds(
            new BaBaBooeyStrap.Core.Models.WindowState { Width = 1000, Height = 700, Left = 100, Top = 100 },
            virtualLeft: 0,
            virtualTop: 0,
            virtualWidth: 500,
            virtualHeight: 400);

        tiny.Width.Should().Be(500);
        tiny.Height.Should().Be(400);
        tiny.Left.Should().Be(0);
        tiny.Top.Should().Be(0);

        var multiMonitor = MainWindowViewModel.ClampWindowBounds(
            new BaBaBooeyStrap.Core.Models.WindowState { Width = 1100, Height = 700, Left = -5000, Top = 3000 },
            virtualLeft: -1920,
            virtualTop: -200,
            virtualWidth: 3840,
            virtualHeight: 1200);

        multiMonitor.Left.Should().BeGreaterThanOrEqualTo(-1920);
        multiMonitor.Top.Should().Be(300);
    }

    [Fact]
    public async Task ActivityWatcher_HistoryIsCappedAt100Entries()
    {
        string logPath = Path.Combine(_tempDir, "history_cap.log");
        File.WriteAllText(logPath, string.Empty);

        using var watcher = new ActivityWatcher(options: new ActivityWatcherOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(15),
            RpcRateLimit = TimeSpan.Zero
        });

        int leavesObserved = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.GameLeft += (_, _) =>
        {
            if (Interlocked.Increment(ref leavesObserved) >= 110)
            {
                allDone.TrySetResult();
            }
        };

        watcher.StartWatching(logPath);

        using (var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = new StreamWriter(fs) { AutoFlush = true })
        {
            for (int i = 1; i <= 110; i++)
            {
                writer.WriteLine($"! Joining game '00000000-0000-0000-0000-{i:D12}' place {i} at 128.116.10.1");
                writer.WriteLine("serverId: 128.116.10.1|50000");
                writer.WriteLine("[FLog::Network] Time to disconnect replication data: 0.05");
            }
        }

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        watcher.History.Should().HaveCount(100);
        watcher.History[0].PlaceId.Should().Be("110");
        watcher.History[^1].PlaceId.Should().Be("11");
    }

    [Fact]
    public void LocalizationCatalogs_HaveExactKeyAndPlaceholderParity_AndResolveRobloxTheme()
    {
        var loc = LocalizationService.Instance;
        var en = loc.GetCatalog(LocalizationService.EnglishTag);
        var zh = loc.GetCatalog(LocalizationService.TraditionalChineseTag);

        en.Keys.Should().BeEquivalentTo(zh.Keys);

        var placeholderRegex = new Regex(@"\{([0-9]+)(?::[^}]*)?\}", RegexOptions.Compiled);
        foreach (string key in en.Keys)
        {
            var enPlaceholders = placeholderRegex.Matches(en[key]).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);
            var zhPlaceholders = placeholderRegex.Matches(zh[key]).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);
            zhPlaceholders.Should().Equal(enPlaceholders, $"placeholder mismatch on key '{key}'");
        }

        try
        {
            loc.ApplyLanguage(AppLanguage.English);
            loc.GetEnumDisplay(RobloxTheme.Default).Should().Be("Default");
            loc.GetEnumDisplay(RobloxTheme.Light).Should().Be("Light");
            loc.GetEnumDisplay(RobloxTheme.Dark).Should().Be("Dark");

            loc.ApplyLanguage(AppLanguage.TraditionalChinese);
            loc.GetEnumDisplay(RobloxTheme.Default).Should().NotBe("Enum_RobloxTheme_Default");
            loc.GetEnumDisplay(RobloxTheme.Light).Should().NotBe("Enum_RobloxTheme_Light");
            loc.GetEnumDisplay(RobloxTheme.Dark).Should().NotBe("Enum_RobloxTheme_Dark");
        }
        finally
        {
            loc.ApplyLanguage(AppLanguage.English);
        }
    }

    [Fact]
    public void CursorPresetHandler_DetectCursorPreset_MatchesAppliedAssets()
    {
        CursorPresetHandler.DetectCursorPreset(_tempDir).Should().Be(CursorPreset.Default);

        CursorPresetHandler.ApplyCursorPreset(_tempDir, CursorPreset.Classic2006);
        CursorPresetHandler.DetectCursorPreset(_tempDir).Should().Be(CursorPreset.Classic2006);

        CursorPresetHandler.ApplyCursorPreset(_tempDir, CursorPreset.Classic2013);
        CursorPresetHandler.DetectCursorPreset(_tempDir).Should().Be(CursorPreset.Classic2013);

        CursorPresetHandler.ApplyCursorPreset(_tempDir, CursorPreset.Default);
        CursorPresetHandler.DetectCursorPreset(_tempDir).Should().Be(CursorPreset.Default);
    }

    [Fact]
    public void BootstrapperDialogViewModel_Dispose_DisposesOwnedCtsAndLeavesExternalCtsUntouched()
    {
        var ownedVm = new BootstrapperDialogViewModel();
        var ownedToken = ownedVm.CancellationToken;
        ownedToken.IsCancellationRequested.Should().BeFalse();

        ownedVm.Dispose();
        ownedToken.IsCancellationRequested.Should().BeTrue();
        ownedVm.Dispose(); // idempotent

        using var externalCts = new CancellationTokenSource();
        var borrowedVm = new BootstrapperDialogViewModel(externalCts);
        borrowedVm.Dispose();
        externalCts.IsCancellationRequested.Should().BeFalse();
        externalCts.Cancel();
        externalCts.IsCancellationRequested.Should().BeTrue();
    }
}
