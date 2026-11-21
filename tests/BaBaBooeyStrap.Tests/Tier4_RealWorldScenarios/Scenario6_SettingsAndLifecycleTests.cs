using System.Text;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier4_RealWorldScenarios;

public sealed class Scenario6_SettingsAndLifecycleTests
{
    [Fact]
    public void Scenario6_FirstRunLifecycle_InitializesPathsSettingsStateAndStructuredLogs()
    {
        // Arrange: Clean user sandbox
        using var env = new MockRobloxEnvironment();
        var paths = new PathProvider(env.BaBaBooeyStrapPath);
        paths.EnsureDirectoriesExist();

        string logFilePath;
        using (var logger = new Logger { MinLevel = LogLevel.Debug })
        {
            logger.Initialize(paths.Logs);
            logFilePath = logger.FileLocation!;
            logger.Info("Bootstrap", $"Initialized paths at {Path.Combine(Paths.UserProfile, "AppData", "Local", "BaBaBooeyStrap")}");

            // Configure Settings (F2)
            var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
            settingsMgr.Load();
            settingsMgr.Prop.Theme = Theme.Dark;
            settingsMgr.Prop.BootstrapperStyle = BootstrapperStyle.ByfronDialog;
            settingsMgr.Prop.MultiInstanceLaunchDelayMs = 2000;
            settingsMgr.Save();

            // Record AppState & RobloxState (F2)
            var appStateMgr = new JsonManager<AppState>(paths.StateFile);
            appStateMgr.Load();
            appStateMgr.Prop.VersionGuid = "version-scenario6-init";
            appStateMgr.Prop.Version = "0.642.0.100";
            appStateMgr.Prop.PackageHashes["RobloxApp.zip"] = "abc123def456";
            appStateMgr.Save();

            var rbxStateMgr = new JsonManager<RobloxState>(paths.RobloxStateFile);
            rbxStateMgr.Load();
            rbxStateMgr.Prop.Player.VersionGuid = "version-scenario6-init";
            rbxStateMgr.Prop.ModManifest.Add("content/sounds/ouch.ogg");
            rbxStateMgr.Save();

            logger.Info("Lifecycle", "Persisted initial configuration and state manifests");
            logger.Flush();
        }

        // Assert: Reload all JSON state files & inspect log file on disk
        var reloadedSettings = new JsonManager<Settings>(paths.SettingsFile);
        reloadedSettings.Load();
        reloadedSettings.Prop.Theme.Should().Be(Theme.Dark);
        reloadedSettings.Prop.BootstrapperStyle.Should().Be(BootstrapperStyle.ByfronDialog);
        reloadedSettings.Prop.MultiInstanceLaunchDelayMs.Should().Be(2000);

        var reloadedAppState = new JsonManager<AppState>(paths.StateFile);
        reloadedAppState.Load();
        reloadedAppState.Prop.VersionGuid.Should().Be("version-scenario6-init");
        reloadedAppState.Prop.PackageHashes.Should().ContainKey("RobloxApp.zip");

        var reloadedRbxState = new JsonManager<RobloxState>(paths.RobloxStateFile);
        reloadedRbxState.Load();
        reloadedRbxState.Prop.Player.VersionGuid.Should().Be("version-scenario6-init");
        reloadedRbxState.Prop.ModManifest.Should().Contain("content/sounds/ouch.ogg");

        string logContent = File.ReadAllText(logFilePath, Encoding.UTF8);
        logContent.Should().Contain("[Bootstrap]");
        logContent.Should().Contain("[Lifecycle]");
        logContent.Should().Contain("%USERPROFILE%");
        logContent.Should().NotContain(Paths.UserProfile);
    }

    [Fact]
    public void Scenario6_SettingsWindowAndFastFlagEditor_UpdatesThemePersistsSettingsAndDeploysFlags()
    {
        // Arrange: Accounts + Settings + Installed Roblox Version
        using var env = new MockRobloxEnvironment();
        var paths = new PathProvider(env.BaBaBooeyStrapPath);
        paths.EnsureDirectoriesExist();
        string versionDir = env.CreateDummyRobloxInstallation("version-scenario6-ui");

        var accountStore = new DpapiAccountStore(paths.AccountsFile);
        accountStore.Upsert(new RobloxAccount { UserId = 42001, Username = "Scenario6User", Cookie = "sec_cookie_42" });

        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        settingsMgr.Load();

        // Act 1: User edits theme & acrylic settings via MainWindowViewModel (F22)
        var mainVm = new MainWindowViewModel(settingsMgr.Prop, accountStore);
        Theme? observedTheme = null;
        mainVm.ThemeChanged += (_, t) => observedTheme = t;
        mainVm.CurrentTheme = Theme.Light;
        mainVm.UseAcrylicBackground = true;
        mainVm.AcrylicBackgroundOpacity = 180;
        mainVm.SaveSettings(settingsMgr);

        // Act 2: User edits FastFlags in FastFlagEditorViewModel (F23), exports to Modifications, overlays onto version (F12, F13)
        var editorVm = new FastFlagEditorViewModel();
        editorVm.AddFlag("DFIntTaskSchedulerTargetFps", "240");
        editorVm.AddFlag("FFlagDebugDisplayFPS", "True");
        editorVm.EditFlag("DFIntTaskSchedulerTargetFps", "165");
        editorVm.SearchText = "TargetFps";
        editorVm.FilteredFlags.Should().ContainSingle(f => f.Name == "DFIntTaskSchedulerTargetFps" && f.Value == "165");

        string modClientSettings = Path.Combine(paths.Modifications, "ClientSettings", "ClientAppSettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(modClientSettings)!);
        File.WriteAllText(modClientSettings, editorVm.ExportJson(), Encoding.UTF8);

        var appliedManifest = ModOverlayEngine.ApplyModifications(paths.Modifications, versionDir);

        var rbxStateMgr = new JsonManager<RobloxState>(paths.RobloxStateFile);
        rbxStateMgr.Prop.ModManifest.AddRange(appliedManifest);
        rbxStateMgr.Save();

        // Assert
        observedTheme.Should().Be(Theme.Light);
        mainVm.Accounts.Should().ContainSingle(a => a.Username == "Scenario6User");

        var reloadedSettings = new JsonManager<Settings>(paths.SettingsFile);
        reloadedSettings.Load();
        reloadedSettings.Prop.Theme.Should().Be(Theme.Light);
        reloadedSettings.Prop.UseAcrylicBackground.Should().BeTrue();
        reloadedSettings.Prop.AcrylicBackgroundOpacity.Should().Be(180);

        var deployedFf = new FastFlagManager(Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json"));
        deployedFf.Load();
        deployedFf.Flags["DFIntTaskSchedulerTargetFps"].ToString().Should().Be("165");
        deployedFf.Flags["FFlagDebugDisplayFPS"].ToString().Should().Be("True");
    }

    [Fact]
    public void Scenario6_BootstrapperDialogThemeAndSystemTrayKeepAlive_ManagesBackgroundSession()
    {
        // Arrange: Custom XML theme & tray keep-alive (F1, F2, F24, F25)
        using var env = new MockRobloxEnvironment();
        var paths = new PathProvider(env.BaBaBooeyStrapPath);
        paths.EnsureDirectoriesExist();

        using var logger = new Logger();
        logger.Initialize(paths.Logs);

        string customThemeXml = """
            <Theme Name="MidnightSlate">
              <Colors Background="#FF141820" Foreground="#FFE6EDF3"/>
            </Theme>
            """;
        string themeFile = Path.Combine(paths.CustomThemes, "MidnightSlate.xml");
        File.WriteAllText(themeFile, customThemeXml, Encoding.UTF8);

        var theme = XmlThemeParser.Parse(File.ReadAllText(themeFile, Encoding.UTF8));
        theme.Background.Should().Be("#FF141820");
        theme.Foreground.Should().Be("#FFE6EDF3");

        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        settingsMgr.Prop.BootstrapperStyle = BootstrapperStyle.CustomDialog;
        settingsMgr.Prop.BootstrapperTitle = "BaBaBooeyStrap Midnight";
        settingsMgr.Save();

        // Act: Background keep-alive via system tray
        using var tray = new NotifyIconWrapper();
        tray.Initialize();
        tray.IsTrayActive.Should().BeTrue();
        tray.ContextMenuActions.Should().Contain(new[] { "Settings", "Launch", "Exit" });

        tray.MinimizeToTray();
        tray.IsWindowVisible.Should().BeFalse();
        tray.ShowBalloonNotification("BaBaBooeyStrap", "Running in background for multi-instance keep-alive");
        logger.Info("Tray", $"Minimized with notification: {tray.LastBalloonTitle}");

        tray.RestoreWindow();
        tray.IsWindowVisible.Should().BeTrue();
        logger.Flush();

        // Assert
        var reloadedSettings = new JsonManager<Settings>(paths.SettingsFile);
        reloadedSettings.Load();
        reloadedSettings.Prop.BootstrapperStyle.Should().Be(BootstrapperStyle.CustomDialog);
        reloadedSettings.Prop.BootstrapperTitle.Should().Be("BaBaBooeyStrap Midnight");
        logger.History.Should().Contain(h => h.Contains("[Tray]") && h.Contains("Minimized"));
    }

    [Fact]
    public void Scenario6_CorruptedConfigAndAccountsRecovery_LogsWarningAndRestoresSafeDefaults()
    {
        // Arrange: Corrupt Settings.json, State.json, and accounts.dat
        using var env = new MockRobloxEnvironment();
        var paths = new PathProvider(env.BaBaBooeyStrapPath);
        paths.EnsureDirectoriesExist();

        File.WriteAllText(paths.SettingsFile, "{ corrupted_json: [ ");
        File.WriteAllText(paths.StateFile, "{ also_corrupted ");
        File.WriteAllBytes(paths.AccountsFile, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        using var logger = new Logger { MinLevel = LogLevel.Warn };
        logger.Initialize(paths.Logs);

        int loadErrors = 0;
        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        settingsMgr.OnLoadError += (_, msg) =>
        {
            loadErrors++;
            logger.Warn("ConfigRecovery", $"Recovered corrupted settings: {msg}");
        };

        var stateMgr = new JsonManager<AppState>(paths.StateFile);
        stateMgr.OnLoadError += (_, msg) =>
        {
            loadErrors++;
            logger.Warn("StateRecovery", $"Recovered corrupted state: {msg}");
        };

        // Act
        settingsMgr.Load();
        stateMgr.Load();

        var accountStore = new DpapiAccountStore(paths.AccountsFile);
        var mainVm = new MainWindowViewModel(settingsMgr.Prop, accountStore);

        var editorVm = new FastFlagEditorViewModel();
        editorVm.AddFlag("FFlagKeepSafe", "True");
        bool malformedImportAccepted = editorVm.ImportJson("{ broken json ");
        logger.Flush();

        // Assert
        loadErrors.Should().Be(2);
        settingsMgr.Prop.BootstrapperTitle.Should().Be("BaBaBooeyStrap");
        mainVm.Accounts.Should().BeEmpty();
        malformedImportAccepted.Should().BeFalse();
        editorVm.Flags.Should().ContainSingle(f => f.Name == "FFlagKeepSafe");

        Directory.GetFiles(paths.Base, "*.corrupted.*.bak").Length.Should().Be(2);
        logger.History.Count.Should().Be(2);
    }
}
