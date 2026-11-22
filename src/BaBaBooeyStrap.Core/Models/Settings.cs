using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.Core.Models;

public class Settings
{
    // Bootstrapper Appearance & Identity
    public BootstrapperStyle BootstrapperStyle { get; set; } = BootstrapperStyle.FluentAeroDialog;
    public BootstrapperIcon BootstrapperIcon { get; set; } = BootstrapperIcon.IconBloxstrap;
    public string BootstrapperTitle { get; set; } = "BaBaBooeyStrap";
    public string BootstrapperIconCustomLocation { get; set; } = string.Empty;
    public RobloxIcon RobloxIcon { get; set; } = RobloxIcon.IconDefault;
    public string RobloxTitle { get; set; } = "Roblox";
    public string RobloxIconCustomLocation { get; set; } = string.Empty;
    public Theme Theme { get; set; } = Theme.Default;
    public AppLanguage Language { get; set; } = AppLanguage.Auto;
    public bool UseAcrylicBackground { get; set; } = false;
    public byte AcrylicBackgroundOpacity { get; set; } = 165;
    public string? SelectedCustomTheme { get; set; }

    // Bootstrapper Behavior & Updates
    public bool CheckForUpdates { get; set; } = true;
    public bool CheckAppUpdates { get; set; } = true;
    public bool RunOnStartup { get; set; } = false;
    public bool ConfirmLaunches { get; set; } = true;
    public bool BackgroundUpdatesEnabled { get; set; } = false;
    public bool ForceLocalData { get; set; } = false;
    public bool StaticDirectory { get; set; } = false;
    public bool AllowCookieAccess { get; set; } = false;
    public bool WPFSoftwareRender { get; set; } = false;
    public bool EnableAnalytics { get; set; } = false;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DeveloperMode { get; set; } = false;
    public bool DebugDisableVersionPackageCleanup { get; set; } = false;

    // Roblox Channel & Environment
    public string Channel { get; set; } = "production";
    public string RobloxDomain { get; set; } = "roblox.com";
    public ChannelChangeMode ChannelChangeMode { get; set; } = ChannelChangeMode.Automatic;
    public DistributorType DistributorType { get; set; } = DistributorType.Global;
    public WebEnvironment WebEnvironment { get; set; } = WebEnvironment.Production;
    public string Locale { get; set; } = "nil";
    public bool ForceRobloxLanguage { get; set; } = false;
    public RobloxTheme RobloxTheme { get; set; } = RobloxTheme.Default;
    public bool EnableRobloxBackgroundApp { get; set; } = false;

    // FastFlags & Tweaks
    public bool UseFastFlagManager { get; set; } = true;
    public bool UseDisableAppPatch { get; set; } = false;

    // Matchmaking
    public bool EnableBetterMatchmaking { get; set; } = false;
    public bool EnableBetterMatchmakingRandomization { get; set; } = false;

    // Cleaner Options
    public CleanerOptions CleanerOptions { get; set; } = CleanerOptions.TwoWeeks;
    public List<string> CleanerDirectories { get; set; } = ["RobloxCache", "RobloxStudioCache", "RobloxLogs", "BaBaBooeyStrapLogs"];

    // Integrations & Discord RPC
    public bool EnableWindowManipulation { get; set; } = false;
    public bool FakeBorderlessFullscreen { get; set; } = false;
    public bool EnableActivityTracking { get; set; } = true;
    public bool UseDiscordRichPresence { get; set; } = true;
    public DiscordRPCStatusDisplay RichPresenceStatusDisplayType { get; set; } = DiscordRPCStatusDisplay.Name;
    public bool HideRPCButtons { get; set; } = true;
    public bool ShowAccountOnRichPresence { get; set; } = false;
    public bool ShowServerDetails { get; set; } = true;
    public ObservableCollection<CustomIntegration> CustomIntegrations { get; set; } = [];

    // Multi-Instance Integration Options (from Multi_Roblox)
    public bool MultiInstanceEnabled { get; set; } = true;
    public int MultiInstanceLaunchDelayMs { get; set; } = 1500;
    public bool MinimizeToTray { get; set; } = true;

    public const string LegacyMultiInstanceWindowTitleTemplate = "[#{Index}] {Account} - {OriginalTitle}";
    public const string DefaultMultiInstanceWindowTitleTemplate = "[#{Index}] [{Server}] {Account} - {OriginalTitle}";

    // Multi-Instance Window Switcher & Tiling
    public bool RestoreAccountWindowBounds { get; set; } = true;
    public bool AutoTagMultiInstanceWindowTitle { get; set; } = true;
    public string MultiInstanceWindowTitleTemplate { get; set; } = DefaultMultiInstanceWindowTitleTemplate;
    public bool EnableQuickSwitcherHotkey { get; set; } = true;
    public string QuickSwitcherHotkey { get; set; } = "Ctrl+Alt+Space";
    public bool EnableCycleWindowHotkey { get; set; } = true;
    public string CycleWindowHotkey { get; set; } = "Ctrl+Alt+Right";
    public bool EnableCyclePreviousWindowHotkey { get; set; } = true;
    public string CyclePreviousWindowHotkey { get; set; } = "Ctrl+Alt+Left";
    public bool EnableDirectSlotHotkeys { get; set; } = true;
    public string DirectSlotHotkeyModifier { get; set; } = "Ctrl+Alt";
    public bool EnableWindowSwitchHighlight { get; set; } = true;
    public bool FloatingSwitcherBarEnabled { get; set; } = true;
    public bool FloatingSwitcherBarAutoShow { get; set; } = true;
    public WindowTilingMode DefaultWindowTilingMode { get; set; } = WindowTilingMode.SingleMonitorGrid;
    public string? PreferredTilingMonitorDevice { get; set; }
    public string? MainSidePrimaryMonitorDevice { get; set; }
    public string? MainSideSecondaryMonitorDevice { get; set; }
}
