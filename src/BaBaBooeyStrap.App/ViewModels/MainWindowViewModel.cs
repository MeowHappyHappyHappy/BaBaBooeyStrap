using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Data;
using BaBaBooeyStrap.App.Models;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Bootstrapper.Mods.Presets;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.FastFlags;
using BaBaBooeyStrap.Core.Launch;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Cleaner;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.MultiInstance.Abstractions;

namespace BaBaBooeyStrap.App.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private const string HexSuccess = "#FF10B981";
    private const string HexWarning = "#FFF59E0B";
    private const string HexDanger = "#FFEF4444";
    private const string HexMuted = "#FF9CA3AF";

    private readonly Settings _settings;
    private readonly IPaths _paths;
    private readonly IDpapiAccountStore? _accountStore;
    private readonly IJoinTargetStore _joinTargetStore;
    private readonly IMultiInstanceController? _multiInstanceController;
    private readonly IProcessSupervisor? _processSupervisor;
    private readonly IRobloxLauncher? _launcher;
    private readonly IGlobalSettingsManager _globalSettingsManager;
    private readonly FastFlagManager _fastFlagManager;
    private readonly IJsonManager<State>? _stateManager;
    private readonly IJsonManager<RobloxState>? _robloxStateManager;
    private readonly InstallerService _installerService;
    private readonly StringBuilder _activityLogBuilder = new();

    private RobloxAccount? _selectedAccount;
    private AccountItem? _selectedAccountItem;
    private SavedJoinTarget? _selectedSavedTarget;
    private string _accountSearchText = string.Empty;
    private string _targetName = string.Empty;
    private string _launchTargetInput = string.Empty;
    private string _launchJobId = string.Empty;
    private bool _isFollowUserTargetMode;
    private string _launchPlaceIdText = string.Empty;
    private string _launchShareLink = string.Empty;
    private string _multiInstanceStatusText = "Multi-Instance: Unknown";
    private string _runningPidsText = "Running clients: 0";
    private string _runningPidsToolTip = string.Empty;
    private bool _ownsMutex;
    private bool _waitingForMutex;
    private string _mutexBadgeText = string.Empty;
    private string _mutexToolTip = string.Empty;
    private string _mutexIndicatorHex = HexMuted;
    private bool _isProtocolRegistered;
    private string _protocolStatusText = string.Empty;
    private string _protocolToolTip = string.Empty;
    private string _protocolIndicatorHex = HexMuted;
    private bool _isLaunching;
    private CancellationTokenSource? _launchCts;
    private string? _lastLoggedMutexState;
    private string? _lastLoggedMutexError;
    private string _statusMessage = LocalizationService.Instance.GetString("Str_Common_Ready");
    private string _statusMessageKey = "Str_Common_Ready";
    private object?[] _statusMessageArgs = Array.Empty<object?>();

    // FastFlags Tab state
    private RenderingMode _selectedRenderingMode = RenderingMode.Default;
    private MSAAMode _selectedMSAAMode = MSAAMode.Default;
    private int _frmQualityOverride = 1;
    private int _meshLodLevel = 3;
    private bool _disableDpiScaling;
    private bool _manualFullscreen = true;

    // Mods Tab state
    private string _currentFontStatus = LocalizationService.Instance.GetString("Str_Mods_DefaultFontStatus");
    private string _fontStatusKey = "Str_Mods_DefaultFontStatus";
    private object?[] _fontStatusArgs = Array.Empty<object?>();
    private CursorPreset _selectedCursorPreset = CursorPreset.Default;
    private bool _hasCustomDeathSound;
    private bool _oldAvatarBackgroundEnabled;
    private int _activeModFileCount;

    // Appearance Tab state
    private ThemeDefinition _previewTheme = XmlThemeParser.GetDefaultFluent();

    // Behaviour / Cleaner state
    private string _cleanerStatusText = string.Empty;
    private string? _cleanerStatusKey;
    private object?[] _cleanerStatusArgs = Array.Empty<object?>();

    // Channel Tab state
    private string _cdnTestResult = LocalizationService.Instance.GetString("Str_Common_NotTestedYet");
    private string _cdnTestResultKey = "Str_Common_NotTestedYet";
    private object?[] _cdnTestResultArgs = Array.Empty<object?>();
    private bool _forceReinstall;
    private readonly EventHandler<string> _languageChangedHandler;

    public MainWindowViewModel(
        Settings settings,
        IDpapiAccountStore? accountStore = null,
        IMultiInstanceController? multiInstanceController = null,
        IProcessSupervisor? processSupervisor = null,
        IRobloxLauncher? launcher = null,
        IPaths? paths = null,
        IGlobalSettingsManager? globalSettingsManager = null,
        IJsonManager<State>? stateManager = null,
        IJsonManager<RobloxState>? robloxStateManager = null,
        IJoinTargetStore? joinTargetStore = null)
    {
        _settings = settings ?? new Settings();
        _paths = paths ?? Paths.Current;
        _accountStore = accountStore;
        _joinTargetStore = joinTargetStore ?? new JoinTargetStore(_paths.TargetsFile);
        _multiInstanceController = multiInstanceController;
        _processSupervisor = processSupervisor ?? new ProcessSupervisor();
        _launcher = launcher;
        _globalSettingsManager = globalSettingsManager ?? new GlobalSettingsManager(_paths.RobloxGlobalSettings);
        _fastFlagManager = new FastFlagManager(_paths.GetClientSettingsPath());
        _stateManager = stateManager;
        _robloxStateManager = robloxStateManager;
        _installerService = new InstallerService(_paths, _stateManager);

        FilteredAccountItems = CollectionViewSource.GetDefaultView(AccountItems);
        FilteredAccountItems.Filter = FilterAccountItem;

        ReloadAccounts();
        ReloadSavedTargets();
        RestoreSavedTargetState();
        LoadFastFlagPresets();
        RefreshModStatus();
        RefreshCustomThemes();
        LoadGlobalSettingsSafe();
        RefreshProtocolStatus();
        RefreshMultiInstanceStatus();

        if (_stateManager != null)
        {
            _forceReinstall = _stateManager.Prop.ForceReinstall;
        }

        _languageChangedHandler = (_, _) => RefreshLocalizedProperties();
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
    }

    public Settings Settings => _settings;
    public TimeSpan WindowWaitTimeout { get; set; } = ProcessSupervisor.DefaultWindowWaitTimeout;

    public ObservableCollection<string> NavigationTabs { get; } = new()
    {
        "Accounts", "FastFlags", "Mods", "Appearance", "Behaviour", "Channel", "Global Settings", "Shortcuts", "About"
    };

    public ObservableCollection<RobloxAccount> Accounts { get; } = new();
    public ObservableCollection<AccountItem> AccountItems { get; } = new();
    public ICollectionView FilteredAccountItems { get; }
    public ObservableCollection<SavedJoinTarget> SavedTargets { get; } = new();
    public ObservableCollection<string> AvailableCustomThemes { get; } = new();

    public event EventHandler<Theme>? ThemeChanged;
    public event EventHandler<AppLanguage>? LanguageChanged;
    public event EventHandler? ActivityLogAppended;

    // --- Core Contract Properties (Feature 22) ---
    public Theme CurrentTheme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme != value)
            {
                _settings.Theme = value;
                OnPropertyChanged();
                ThemeChanged?.Invoke(this, value);
            }
            else
            {
                ThemeChanged?.Invoke(this, value);
            }
        }
    }

    public AppLanguage CurrentLanguage
    {
        get => _settings.Language;
        set
        {
            if (_settings.Language != value)
            {
                _settings.Language = value;
                OnPropertyChanged();
            }

            LocalizationService.Instance.ApplyLanguage(value);
            LanguageChanged?.Invoke(this, value);
        }
    }

    public byte AcrylicBackgroundOpacity
    {
        get => _settings.AcrylicBackgroundOpacity;
        set
        {
            if (_settings.AcrylicBackgroundOpacity != value)
            {
                _settings.AcrylicBackgroundOpacity = value;
                OnPropertyChanged();
            }
        }
    }

    public bool UseAcrylicBackground
    {
        get => _settings.UseAcrylicBackground;
        set
        {
            if (_settings.UseAcrylicBackground != value)
            {
                _settings.UseAcrylicBackground = value;
                OnPropertyChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    // --- Tab 1: Accounts & Multi-Instance ---
    public bool MultiInstanceEnabled
    {
        get => _settings.MultiInstanceEnabled;
        set
        {
            if (_settings.MultiInstanceEnabled != value)
            {
                _settings.MultiInstanceEnabled = value;
                OnPropertyChanged();
                ApplyMultiInstanceSetting();
            }
        }
    }

    public int MultiInstanceLaunchDelayMs
    {
        get => _settings.MultiInstanceLaunchDelayMs;
        set
        {
            int clamped = Math.Max(500, value);
            if (_settings.MultiInstanceLaunchDelayMs != clamped)
            {
                _settings.MultiInstanceLaunchDelayMs = clamped;
                OnPropertyChanged();
            }
        }
    }

    public string MultiInstanceStatusText
    {
        get => _multiInstanceStatusText;
        private set => SetProperty(ref _multiInstanceStatusText, value);
    }

    public string RunningPidsText
    {
        get => _runningPidsText;
        private set => SetProperty(ref _runningPidsText, value);
    }

    public bool OwnsMutex
    {
        get => _ownsMutex;
        private set => SetProperty(ref _ownsMutex, value);
    }

    public bool WaitingForMutex
    {
        get => _waitingForMutex;
        private set => SetProperty(ref _waitingForMutex, value);
    }

    public string MutexBadgeText
    {
        get => _mutexBadgeText;
        private set => SetProperty(ref _mutexBadgeText, value);
    }

    public string MutexToolTip
    {
        get => _mutexToolTip;
        private set => SetProperty(ref _mutexToolTip, value);
    }

    public string MutexIndicatorHex
    {
        get => _mutexIndicatorHex;
        private set => SetProperty(ref _mutexIndicatorHex, value);
    }

    public string RunningPidsToolTip
    {
        get => _runningPidsToolTip;
        private set => SetProperty(ref _runningPidsToolTip, value);
    }

    public bool IsProtocolRegistered
    {
        get => _isProtocolRegistered;
        private set => SetProperty(ref _isProtocolRegistered, value);
    }

    public string ProtocolStatusText
    {
        get => _protocolStatusText;
        private set => SetProperty(ref _protocolStatusText, value);
    }

    public string ProtocolToolTip
    {
        get => _protocolToolTip;
        private set => SetProperty(ref _protocolToolTip, value);
    }

    public string ProtocolIndicatorHex
    {
        get => _protocolIndicatorHex;
        private set => SetProperty(ref _protocolIndicatorHex, value);
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            if (_settings.MinimizeToTray != value)
            {
                _settings.MinimizeToTray = value;
                OnPropertyChanged();
            }
        }
    }

    public RobloxAccount? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value))
            {
                var matchingItem = value is null
                    ? null
                    : AccountItems.FirstOrDefault(i => ReferenceEquals(i.Account, value) || i.Id == value.Id);
                if (!ReferenceEquals(_selectedAccountItem, matchingItem))
                {
                    _selectedAccountItem = matchingItem;
                    OnPropertyChanged(nameof(SelectedAccountItem));
                }
            }
        }
    }

    public AccountItem? SelectedAccountItem
    {
        get => _selectedAccountItem;
        set
        {
            if (SetProperty(ref _selectedAccountItem, value))
            {
                var account = value?.Account;
                if (!ReferenceEquals(_selectedAccount, account))
                {
                    _selectedAccount = account;
                    OnPropertyChanged(nameof(SelectedAccount));
                }
            }
        }
    }

    public string AccountSearchText
    {
        get => _accountSearchText;
        set
        {
            if (SetProperty(ref _accountSearchText, value ?? string.Empty))
            {
                FilteredAccountItems.Refresh();
            }
        }
    }

    public string AccountCountText => $"({AccountItems.Count})";

    public SavedJoinTarget? SelectedSavedTarget
    {
        get => _selectedSavedTarget;
        set
        {
            if (SetProperty(ref _selectedSavedTarget, value) && value is not null)
            {
                TargetName = value.Name;
                LaunchTargetInput = value.TargetInput;
                LaunchJobId = value.JobId ?? string.Empty;
                try
                {
                    var parsed = RobloxLauncher.ParseTargetInput(value.TargetInput);
                    IsFollowUserTargetMode = parsed.Kind == LaunchTargetKind.FollowUser;
                }
                catch
                {
                    IsFollowUserTargetMode = false;
                }
            }
        }
    }

    public bool IsFollowUserTargetMode
    {
        get => _isFollowUserTargetMode;
        set
        {
            if (SetProperty(ref _isFollowUserTargetMode, value))
            {
                OnPropertyChanged(nameof(TargetModeSelectedIndex));
                OnPropertyChanged(nameof(IsFollowUserModeSelected));
                OnPropertyChanged(nameof(IsGameServerTargetMode));
                OnPropertyChanged(nameof(IsJobIdInputVisible));
                OnPropertyChanged(nameof(TargetInputLabelText));
                OnPropertyChanged(nameof(TargetInputPlaceholderText));
            }
        }
    }

    public int TargetModeSelectedIndex
    {
        get => _isFollowUserTargetMode ? 1 : 0;
        set => IsFollowUserTargetMode = value == 1;
    }

    public bool IsFollowUserModeSelected
    {
        get => _isFollowUserTargetMode;
        set
        {
            if (value)
            {
                IsFollowUserTargetMode = true;
            }
        }
    }

    public bool IsGameServerTargetMode
    {
        get => !_isFollowUserTargetMode;
        set
        {
            if (value)
            {
                IsFollowUserTargetMode = false;
            }
        }
    }

    public bool IsJobIdInputVisible => !_isFollowUserTargetMode;

    public string TargetInputLabelText => LocalizationService.Instance.GetString(
        _isFollowUserTargetMode ? "Str_Accounts_FollowUserInputLabel" : "Str_Accounts_TargetInputLabel");

    public string TargetInputPlaceholderText => LocalizationService.Instance.GetString(
        _isFollowUserTargetMode ? "Str_Accounts_FollowUserInputPlaceholder" : "Str_Accounts_TargetInputPlaceholder");

    public string TargetName
    {
        get => _targetName;
        set
        {
            if (SetProperty(ref _targetName, value ?? string.Empty) && _stateManager != null)
            {
                _stateManager.Prop.LastTargetName = _targetName.Trim();
            }
        }
    }

    public string LaunchTargetInput
    {
        get => _launchTargetInput;
        set
        {
            if (SetProperty(ref _launchTargetInput, value ?? string.Empty) && _stateManager != null)
            {
                _stateManager.Prop.LastTargetInput = _launchTargetInput.Trim();
            }
        }
    }

    public string LaunchJobId
    {
        get => _launchJobId;
        set
        {
            if (SetProperty(ref _launchJobId, value ?? string.Empty) && _stateManager != null)
            {
                _stateManager.Prop.LastJobId = _launchJobId.Trim();
            }
        }
    }

    public string LaunchPlaceIdText
    {
        get => _launchPlaceIdText;
        set => SetProperty(ref _launchPlaceIdText, value);
    }

    public string LaunchShareLink
    {
        get => _launchShareLink;
        set => SetProperty(ref _launchShareLink, value);
    }

    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (SetProperty(ref _isLaunching, value))
            {
                OnPropertyChanged(nameof(IsNotLaunching));
                OnPropertyChanged(nameof(CanCancelLaunch));
            }
        }
    }

    public bool IsNotLaunching => !IsLaunching;
    public bool CanCancelLaunch => IsLaunching && _launchCts != null;
    public string ActivityLogText => _activityLogBuilder.ToString();

    // --- Tab 2: FastFlags ---
    public bool UseFastFlagManager
    {
        get => _settings.UseFastFlagManager;
        set
        {
            if (_settings.UseFastFlagManager != value)
            {
                _settings.UseFastFlagManager = value;
                OnPropertyChanged();
            }
        }
    }

    public bool UseDisableAppPatch
    {
        get => _settings.UseDisableAppPatch;
        set
        {
            if (_settings.UseDisableAppPatch != value)
            {
                _settings.UseDisableAppPatch = value;
                OnPropertyChanged();
            }
        }
    }

    public RenderingMode SelectedRenderingMode
    {
        get => _selectedRenderingMode;
        set => SetProperty(ref _selectedRenderingMode, value);
    }

    public MSAAMode SelectedMSAAMode
    {
        get => _selectedMSAAMode;
        set => SetProperty(ref _selectedMSAAMode, value);
    }

    public int FrmQualityOverride
    {
        get => _frmQualityOverride;
        set => SetProperty(ref _frmQualityOverride, Math.Clamp(value, 1, 21));
    }

    public int MeshLodLevel
    {
        get => _meshLodLevel;
        set => SetProperty(ref _meshLodLevel, Math.Clamp(value, 0, 3));
    }

    public bool DisableDpiScaling
    {
        get => _disableDpiScaling;
        set => SetProperty(ref _disableDpiScaling, value);
    }

    public bool ManualFullscreen
    {
        get => _manualFullscreen;
        set => SetProperty(ref _manualFullscreen, value);
    }

    // --- Tab 3: Mods ---
    public string CurrentFontStatus
    {
        get => _currentFontStatus;
        private set => SetProperty(ref _currentFontStatus, value);
    }

    public CursorPreset SelectedCursorPreset
    {
        get => _selectedCursorPreset;
        set
        {
            if (SetProperty(ref _selectedCursorPreset, value))
            {
                ApplyCursorPreset(value);
            }
        }
    }

    public bool HasCustomDeathSound
    {
        get => _hasCustomDeathSound;
        private set
        {
            if (SetProperty(ref _hasCustomDeathSound, value))
            {
                OnPropertyChanged(nameof(CustomDeathSoundStatusText));
            }
        }
    }

    public string CustomDeathSoundStatusText =>
        LocalizationService.Instance.Format(
            "Str_Mods_DeathSoundActiveFormat",
            LocalizationService.Instance.GetString(HasCustomDeathSound ? "Str_Common_Yes" : "Str_Common_No"));

    public bool OldAvatarBackgroundEnabled
    {
        get => _oldAvatarBackgroundEnabled;
        set
        {
            if (SetProperty(ref _oldAvatarBackgroundEnabled, value))
            {
                AvatarBackgroundPresetHandler.SetOldAvatarBackground(value, _paths.Modifications);
                RefreshModStatus();
            }
        }
    }

    public int ActiveModFileCount
    {
        get => _activeModFileCount;
        private set => SetProperty(ref _activeModFileCount, value);
    }

    // --- Tab 4: Appearance ---
    public IReadOnlyList<Theme> AvailableThemes { get; } = Enum.GetValues<Theme>();
    public IReadOnlyList<AppLanguage> AvailableLanguages { get; } = Enum.GetValues<AppLanguage>();
    public IReadOnlyList<BootstrapperStyle> AvailableBootstrapperStyles { get; } = Enum.GetValues<BootstrapperStyle>();
    public IReadOnlyList<BootstrapperIcon> AvailableBootstrapperIcons { get; } = Enum.GetValues<BootstrapperIcon>();
    public IReadOnlyList<RobloxIcon> AvailableRobloxIcons { get; } = Enum.GetValues<RobloxIcon>();

    public BootstrapperStyle SelectedBootstrapperStyle
    {
        get => _settings.BootstrapperStyle;
        set { _settings.BootstrapperStyle = value; OnPropertyChanged(); }
    }

    public string BootstrapperTitle
    {
        get => _settings.BootstrapperTitle;
        set { _settings.BootstrapperTitle = value; OnPropertyChanged(); }
    }

    public BootstrapperIcon SelectedBootstrapperIcon
    {
        get => _settings.BootstrapperIcon;
        set { _settings.BootstrapperIcon = value; OnPropertyChanged(); }
    }

    public string BootstrapperIconCustomLocation
    {
        get => _settings.BootstrapperIconCustomLocation;
        set { _settings.BootstrapperIconCustomLocation = value; OnPropertyChanged(); }
    }

    public string? SelectedCustomTheme
    {
        get => _settings.SelectedCustomTheme;
        set
        {
            _settings.SelectedCustomTheme = value;
            OnPropertyChanged();
            UpdatePreviewTheme();
        }
    }

    public ThemeDefinition PreviewTheme
    {
        get => _previewTheme;
        private set => SetProperty(ref _previewTheme, value);
    }

    public string RobloxTitle
    {
        get => _settings.RobloxTitle;
        set { _settings.RobloxTitle = value; OnPropertyChanged(); }
    }

    public RobloxIcon SelectedRobloxIcon
    {
        get => _settings.RobloxIcon;
        set { _settings.RobloxIcon = value; OnPropertyChanged(); }
    }

    public string RobloxIconCustomLocation
    {
        get => _settings.RobloxIconCustomLocation;
        set { _settings.RobloxIconCustomLocation = value; OnPropertyChanged(); }
    }

    // --- Tab 5: Behaviour & Cleaner ---
    public IReadOnlyList<DiscordRPCStatusDisplay> AvailableRpcDisplays { get; } = Enum.GetValues<DiscordRPCStatusDisplay>();
    public IReadOnlyList<CleanerOptions> AvailableCleanerOptions { get; } = Enum.GetValues<CleanerOptions>();

    public bool CheckForUpdates { get => _settings.CheckForUpdates; set { _settings.CheckForUpdates = value; OnPropertyChanged(); } }
    public bool BackgroundUpdatesEnabled { get => _settings.BackgroundUpdatesEnabled; set { _settings.BackgroundUpdatesEnabled = value; OnPropertyChanged(); } }
    public bool ConfirmLaunches { get => _settings.ConfirmLaunches; set { _settings.ConfirmLaunches = value; OnPropertyChanged(); } }
    public bool ForceLocalData { get => _settings.ForceLocalData; set { _settings.ForceLocalData = value; OnPropertyChanged(); } }
    public bool StaticDirectory { get => _settings.StaticDirectory; set { _settings.StaticDirectory = value; OnPropertyChanged(); } }
    public bool WPFSoftwareRender { get => _settings.WPFSoftwareRender; set { _settings.WPFSoftwareRender = value; OnPropertyChanged(); } }
    public bool DeveloperMode { get => _settings.DeveloperMode; set { _settings.DeveloperMode = value; OnPropertyChanged(); } }
    public bool DebugDisableVersionPackageCleanup { get => _settings.DebugDisableVersionPackageCleanup; set { _settings.DebugDisableVersionPackageCleanup = value; OnPropertyChanged(); } }

    public bool EnableActivityTracking { get => _settings.EnableActivityTracking; set { _settings.EnableActivityTracking = value; OnPropertyChanged(); } }
    public bool UseDiscordRichPresence { get => _settings.UseDiscordRichPresence; set { _settings.UseDiscordRichPresence = value; OnPropertyChanged(); } }
    public DiscordRPCStatusDisplay RichPresenceStatusDisplayType { get => _settings.RichPresenceStatusDisplayType; set { _settings.RichPresenceStatusDisplayType = value; OnPropertyChanged(); } }
    public bool HideRPCButtons { get => _settings.HideRPCButtons; set { _settings.HideRPCButtons = value; OnPropertyChanged(); } }
    public bool ShowAccountOnRichPresence { get => _settings.ShowAccountOnRichPresence; set { _settings.ShowAccountOnRichPresence = value; OnPropertyChanged(); } }
    public bool ShowServerDetails { get => _settings.ShowServerDetails; set { _settings.ShowServerDetails = value; OnPropertyChanged(); } }
    public bool EnableWindowManipulation { get => _settings.EnableWindowManipulation; set { _settings.EnableWindowManipulation = value; OnPropertyChanged(); } }
    public bool FakeBorderlessFullscreen { get => _settings.FakeBorderlessFullscreen; set { _settings.FakeBorderlessFullscreen = value; OnPropertyChanged(); } }
    public bool EnableBetterMatchmaking { get => _settings.EnableBetterMatchmaking; set { _settings.EnableBetterMatchmaking = value; OnPropertyChanged(); } }
    public bool EnableBetterMatchmakingRandomization { get => _settings.EnableBetterMatchmakingRandomization; set { _settings.EnableBetterMatchmakingRandomization = value; OnPropertyChanged(); } }

    public CleanerOptions SelectedCleanerOption
    {
        get => _settings.CleanerOptions;
        set { _settings.CleanerOptions = value; OnPropertyChanged(); }
    }

    public bool CleanRobloxCache
    {
        get => HasCleanerDir("RobloxCache");
        set => SetCleanerDir("RobloxCache", value);
    }

    public bool CleanRobloxStudioCache
    {
        get => HasCleanerDir("RobloxStudioCache");
        set => SetCleanerDir("RobloxStudioCache", value);
    }

    public bool CleanRobloxLogs
    {
        get => HasCleanerDir("RobloxLogs");
        set => SetCleanerDir("RobloxLogs", value);
    }

    public bool CleanBaBaBooeyStrapLogs
    {
        get => HasCleanerDir("BaBaBooeyStrapLogs");
        set => SetCleanerDir("BaBaBooeyStrapLogs", value);
    }

    public bool CleanRobloxCrashDumps
    {
        get => HasCleanerDir("RobloxCrashDumps");
        set => SetCleanerDir("RobloxCrashDumps", value);
    }

    public string CleanerStatusText
    {
        get => _cleanerStatusText;
        private set => SetProperty(ref _cleanerStatusText, value);
    }

    public ObservableCollection<CustomIntegration> CustomIntegrations => _settings.CustomIntegrations;

    // --- Tab 6: Channel & CDN ---
    public IReadOnlyList<ChannelChangeMode> AvailableChannelChangeModes { get; } = Enum.GetValues<ChannelChangeMode>();
    public IReadOnlyList<DistributorType> AvailableDistributorTypes { get; } = Enum.GetValues<DistributorType>();
    public IReadOnlyList<WebEnvironment> AvailableWebEnvironments { get; } = Enum.GetValues<WebEnvironment>();
    public IReadOnlyList<RobloxTheme> AvailableRobloxThemes { get; } = Enum.GetValues<RobloxTheme>();
    public IReadOnlyList<string> GlobalMirrors => CdnMirrorSelectorDefaults.GlobalMirrors;

    public string Channel { get => _settings.Channel; set { _settings.Channel = value; OnPropertyChanged(); } }
    public ChannelChangeMode SelectedChannelChangeMode { get => _settings.ChannelChangeMode; set { _settings.ChannelChangeMode = value; OnPropertyChanged(); } }
    public DistributorType SelectedDistributorType { get => _settings.DistributorType; set { _settings.DistributorType = value; OnPropertyChanged(); } }
    public WebEnvironment SelectedWebEnvironment { get => _settings.WebEnvironment; set { _settings.WebEnvironment = value; OnPropertyChanged(); } }
    public string RobloxDomain { get => _settings.RobloxDomain; set { _settings.RobloxDomain = value; OnPropertyChanged(); } }
    public string Locale { get => _settings.Locale; set { _settings.Locale = value; OnPropertyChanged(); } }
    public bool ForceRobloxLanguage { get => _settings.ForceRobloxLanguage; set { _settings.ForceRobloxLanguage = value; OnPropertyChanged(); } }
    public RobloxTheme SelectedRobloxTheme { get => _settings.RobloxTheme; set { _settings.RobloxTheme = value; OnPropertyChanged(); } }
    public bool EnableRobloxBackgroundApp { get => _settings.EnableRobloxBackgroundApp; set { _settings.EnableRobloxBackgroundApp = value; OnPropertyChanged(); } }

    public bool ForceReinstall
    {
        get => _forceReinstall;
        set
        {
            if (SetProperty(ref _forceReinstall, value) && _stateManager != null)
            {
                _stateManager.Prop.ForceReinstall = value;
            }
        }
    }

    public string InstalledPlayerVersion =>
        _robloxStateManager?.Prop.Player.VersionGuid is { Length: > 0 } guid
            ? $"{guid} ({_robloxStateManager.Prop.Player.Version})"
            : LocalizationService.Instance.GetString("Str_Common_NotInstalledYet");

    public string CdnTestResult
    {
        get => _cdnTestResult;
        private set => SetProperty(ref _cdnTestResult, value);
    }

    // --- Tab 7: Global Settings ---
    public int FramerateCap
    {
        get => _globalSettingsManager.FramerateCap;
        set { _globalSettingsManager.FramerateCap = value; OnPropertyChanged(); }
    }

    public bool ReadOnlyLocked
    {
        get => _globalSettingsManager.ReadOnlyLocked;
        set { _globalSettingsManager.ReadOnlyLocked = value; OnPropertyChanged(); }
    }

    public float MouseSensitivity
    {
        get => _globalSettingsManager.MouseSensitivity;
        set { _globalSettingsManager.MouseSensitivity = value; OnPropertyChanged(); }
    }

    public int SavedQualityLevel
    {
        get => _globalSettingsManager.SavedQualityLevel;
        set { _globalSettingsManager.SavedQualityLevel = value; OnPropertyChanged(); }
    }

    public bool VREnabled
    {
        get => _globalSettingsManager.VREnabled;
        set { _globalSettingsManager.VREnabled = value; OnPropertyChanged(); }
    }

    public float PreferredTransparency
    {
        get => _globalSettingsManager.PreferredTransparency;
        set { _globalSettingsManager.PreferredTransparency = value; OnPropertyChanged(); }
    }

    public bool ReducedMotion
    {
        get => _globalSettingsManager.ReducedMotion;
        set { _globalSettingsManager.ReducedMotion = value; OnPropertyChanged(); }
    }

    public int PreferredTextSize
    {
        get => _globalSettingsManager.PreferredTextSize;
        set { _globalSettingsManager.PreferredTextSize = value; OnPropertyChanged(); }
    }

    // --- Tab 8: Shortcuts & Paths ---
    public string BasePath => _paths.Base;
    public string VersionsPath => _paths.Versions;
    public string LogsPath => _paths.Logs;

    // --- Tab 9: About ---
    public string AppVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString()
        ?? "1.0.0.0";

    public string RuntimeInfo => $".NET {Environment.Version} | {(Environment.Is64BitProcess ? "win-x64" : "win-x86")} | {Environment.OSVersion}";

    // --- Methods ---
    public void ReloadAccounts()
    {
        var checkedIds = AccountItems.Where(i => i.IsChecked).Select(i => i.Id).ToHashSet();
        var selectedId = _selectedAccountItem?.Id ?? _selectedAccount?.Id;

        Accounts.Clear();
        AccountItems.Clear();
        if (_accountStore != null)
        {
            try
            {
                var loaded = _accountStore.LoadAll();
                foreach (var acc in loaded)
                {
                    Accounts.Add(acc);
                    var item = new AccountItem(acc)
                    {
                        IsChecked = checkedIds.Contains(acc.Id)
                    };
                    AccountItems.Add(item);
                }
            }
            catch
            {
                // Corrupted DPAPI store renders empty list safely
            }
        }

        if (selectedId.HasValue)
        {
            SelectedAccountItem = AccountItems.FirstOrDefault(i => i.Id == selectedId.Value);
        }
        else if (_selectedAccountItem != null || _selectedAccount != null)
        {
            SelectedAccountItem = null;
        }

        FilteredAccountItems.Refresh();
        OnPropertyChanged(nameof(AccountCountText));
    }

    public void ReloadSavedTargets()
    {
        SavedTargets.Clear();
        try
        {
            foreach (var target in _joinTargetStore.LoadAll())
            {
                SavedTargets.Add(target);
            }
        }
        catch
        {
            // Corrupted target store renders empty list safely
        }
    }

    private void RestoreSavedTargetState()
    {
        if (_stateManager == null) return;

        if (!string.IsNullOrWhiteSpace(_stateManager.Prop.LastTargetName))
        {
            _targetName = _stateManager.Prop.LastTargetName;
            OnPropertyChanged(nameof(TargetName));
        }

        if (!string.IsNullOrWhiteSpace(_stateManager.Prop.LastTargetInput))
        {
            _launchTargetInput = _stateManager.Prop.LastTargetInput;
            OnPropertyChanged(nameof(LaunchTargetInput));
            try
            {
                var parsed = RobloxLauncher.ParseTargetInput(_launchTargetInput);
                if (parsed.Kind == LaunchTargetKind.FollowUser)
                {
                    IsFollowUserTargetMode = true;
                }
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(_stateManager.Prop.LastJobId))
        {
            _launchJobId = _stateManager.Prop.LastJobId;
            OnPropertyChanged(nameof(LaunchJobId));
        }
    }

    private bool FilterAccountItem(object obj)
    {
        if (obj is not AccountItem item) return false;

        var query = _accountSearchText.Trim();
        if (string.IsNullOrWhiteSpace(query)) return true;

        return item.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.UserId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Notes.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public void SaveSettings(IJsonManager<Settings> manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ApplyFastFlagPresets();
        manager.Save();
        if (_stateManager != null)
        {
            _stateManager.Prop.LastTargetName = _targetName.Trim();
            _stateManager.Prop.LastTargetInput = _launchTargetInput.Trim();
            _stateManager.Prop.LastJobId = _launchJobId.Trim();
            _stateManager.Save();
        }
        SetLocalizedStatus("Str_MainWindow_StatusSavedAt", DateTime.Now.ToString("HH:mm:ss"));
    }

    public void RefreshLocalizedProperties()
    {
        StatusMessage = FormatLocalized(_statusMessageKey, _statusMessageArgs);
        CurrentFontStatus = FormatLocalized(_fontStatusKey, _fontStatusArgs);
        if (!string.IsNullOrEmpty(_cleanerStatusKey))
        {
            CleanerStatusText = FormatLocalized(_cleanerStatusKey, _cleanerStatusArgs);
        }
        CdnTestResult = FormatLocalized(_cdnTestResultKey, _cdnTestResultArgs);
        foreach (var item in AccountItems)
        {
            item.Refresh();
        }
        RefreshProtocolStatus();
        RefreshMultiInstanceStatus();
        OnPropertyChanged(nameof(AccountCountText));
        OnPropertyChanged(nameof(CustomDeathSoundStatusText));
        OnPropertyChanged(nameof(InstalledPlayerVersion));
        OnPropertyChanged(nameof(TargetInputLabelText));
        OnPropertyChanged(nameof(TargetInputPlaceholderText));
    }

    internal void DetachLocalization()
    {
        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
    }

    private void SetLocalizedStatus(string key, params object?[] args)
    {
        _statusMessageKey = key;
        _statusMessageArgs = args ?? Array.Empty<object?>();
        StatusMessage = FormatLocalized(_statusMessageKey, _statusMessageArgs);
    }

    private static string FormatLocalized(string key, object?[] args)
    {
        if (args == null || args.Length == 0)
            return LocalizationService.Instance.GetString(key);

        object?[] resolved = new object?[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            resolved[i] = args[i] is Enum e
                ? LocalizationService.Instance.GetEnumDisplay(e)
                : args[i];
        }

        return LocalizationService.Instance.Format(key, resolved);
    }

    public static Core.Models.WindowState ClampWindowBounds(
        Core.Models.WindowState state,
        double virtualLeft,
        double virtualTop,
        double virtualWidth,
        double virtualHeight)
    {
        ArgumentNullException.ThrowIfNull(state);

        double width = state.Width is >= 640 and <= 3840 ? state.Width : 1040;
        double height = state.Height is >= 480 and <= 2160 ? state.Height : 680;

        double maxLeft = Math.Max(virtualLeft, virtualLeft + virtualWidth - width);
        double maxTop = Math.Max(virtualTop, virtualTop + virtualHeight - height);

        double left = Math.Clamp(state.Left, Math.Max(0, virtualLeft), Math.Max(0, maxLeft));
        double top = Math.Clamp(state.Top, Math.Max(0, virtualTop), Math.Max(0, maxTop));

        return new Core.Models.WindowState
        {
            Width = width,
            Height = height,
            Left = left,
            Top = top
        };
    }

    public void RefreshProtocolStatus()
    {
        try
        {
            bool registered = RobloxLauncher.IsRobloxProtocolRegistered();
            IsProtocolRegistered = registered;
            ProtocolIndicatorHex = registered ? HexSuccess : HexDanger;
            ProtocolStatusText = LocalizationService.Instance.GetString(
                registered ? "Str_Accounts_ProtocolRegistered" : "Str_Accounts_ProtocolMissing");
            ProtocolToolTip = LocalizationService.Instance.GetString(
                registered ? "Str_Accounts_ProtocolRegisteredTip" : "Str_Accounts_ProtocolMissingTip");
        }
        catch (Exception ex)
        {
            IsProtocolRegistered = false;
            ProtocolIndicatorHex = HexWarning;
            ProtocolStatusText = LocalizationService.Instance.GetString("Str_Accounts_ProtocolUnknown");
            ProtocolToolTip = ex.Message;
        }
    }

    public void RefreshMultiInstanceStatus()
    {
        if (_multiInstanceController != null)
        {
            var status = _multiInstanceController.GetStatus();
            OwnsMutex = status.OwnsMutex;
            WaitingForMutex = status.WaitingForMutex;

            string mutexLabel = status.OwnsMutex
                ? LocalizationService.Instance.GetString("Str_Accounts_MutexHeldActive")
                : (status.WaitingForMutex
                    ? LocalizationService.Instance.GetString("Str_Accounts_MutexBlocked")
                    : (status.Enabled
                        ? LocalizationService.Instance.GetString("Str_Accounts_MutexNotAcquired")
                        : LocalizationService.Instance.GetString("Str_Common_Disabled")));
            MultiInstanceStatusText = LocalizationService.Instance.Format("Str_Accounts_MultiInstanceStatusFormat", mutexLabel);
            RunningPidsText = LocalizationService.Instance.Format("Str_Accounts_RunningClientsFormat", status.RunningRobloxPids.Count);
            RunningPidsToolTip = status.RunningRobloxPids.Count > 0
                ? LocalizationService.Instance.Format("Str_Accounts_RunningPidsTipFormat", string.Join(", ", status.RunningRobloxPids))
                : LocalizationService.Instance.GetString("Str_Accounts_NoRunningPidsTip");

            string badgeLabel;
            string badgeHex;
            if (!status.Enabled)
            {
                badgeLabel = LocalizationService.Instance.GetString("Str_Accounts_MutexLabelDisabled");
                badgeHex = HexMuted;
            }
            else if (status.OwnsMutex)
            {
                badgeLabel = LocalizationService.Instance.GetString("Str_Accounts_MutexLabelHeld");
                badgeHex = HexSuccess;
            }
            else if (status.WaitingForMutex)
            {
                badgeLabel = LocalizationService.Instance.GetString("Str_Accounts_MutexLabelWaiting");
                badgeHex = HexWarning;
            }
            else
            {
                badgeLabel = LocalizationService.Instance.GetString("Str_Accounts_MutexLabelEnabled");
                badgeHex = HexWarning;
            }

            MutexBadgeText = badgeLabel;
            MutexIndicatorHex = badgeHex;
            MutexToolTip = string.IsNullOrWhiteSpace(status.LastError)
                ? badgeLabel
                : LocalizationService.Instance.Format("Str_Accounts_MutexLabelErrorFormat", badgeLabel, status.LastError);

            bool stateChanged = !string.Equals(_lastLoggedMutexState, badgeLabel, StringComparison.Ordinal);
            if (stateChanged)
            {
                if (_lastLoggedMutexState is not null)
                {
                    AppendActivityLog(FormatMultiInstanceStatusLog(
                        LocalizationService.Instance.Format("Str_Accounts_LogMutexChangedFormat", badgeLabel),
                        status));
                }

                _lastLoggedMutexState = badgeLabel;
            }

            if (!string.IsNullOrWhiteSpace(status.LastError) &&
                !string.Equals(_lastLoggedMutexError, status.LastError, StringComparison.Ordinal))
            {
                if (!stateChanged)
                {
                    AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogMutexErrorFormat", status.LastError));
                }
            }

            _lastLoggedMutexError = status.LastError;
        }
        else
        {
            var pids = _processSupervisor?.GetRunningRobloxPids() ?? Array.Empty<int>();
            OwnsMutex = false;
            WaitingForMutex = false;
            string stateLabel = LocalizationService.Instance.GetString(_settings.MultiInstanceEnabled ? "Str_Common_Enabled" : "Str_Common_Disabled");
            MultiInstanceStatusText = LocalizationService.Instance.Format("Str_Accounts_MultiInstanceStatusFormat", stateLabel);
            RunningPidsText = LocalizationService.Instance.Format("Str_Accounts_RunningClientsFormat", pids.Count);
            RunningPidsToolTip = pids.Count > 0
                ? LocalizationService.Instance.Format("Str_Accounts_RunningPidsTipFormat", string.Join(", ", pids))
                : LocalizationService.Instance.GetString("Str_Accounts_NoRunningPidsTip");
            MutexBadgeText = stateLabel;
            MutexIndicatorHex = _settings.MultiInstanceEnabled ? HexWarning : HexMuted;
            MutexToolTip = stateLabel;
        }
    }

    private void ApplyMultiInstanceSetting()
    {
        if (_multiInstanceController == null) return;
        if (_settings.MultiInstanceEnabled)
        {
            var status = _multiInstanceController.Enable();
            _lastLoggedMutexError = status.LastError;
            AppendActivityLog(FormatMultiInstanceStatusLog(
                LocalizationService.Instance.GetString("Str_Accounts_LogMutexEnabledPrefix"),
                status));
        }
        else
        {
            _multiInstanceController.Disable();
            AppendActivityLog(LocalizationService.Instance.GetString("Str_Accounts_LogMutexDisabled"));
        }

        RefreshMultiInstanceStatus();
    }

    public void AddOrUpdateAccount(RobloxAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var existingItem = AccountItems.FirstOrDefault(a =>
            (account.UserId > 0 && a.UserId == account.UserId) || a.Id == account.Id);

        if (existingItem != null)
        {
            var existing = existingItem.Account;
            existing.Username = account.Username;
            existing.DisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName;
            existing.UserId = account.UserId;
            existing.Cookie = account.Cookie;
            if (!string.IsNullOrWhiteSpace(account.Notes))
            {
                existing.Notes = account.Notes;
            }
            if (account.RobloxSettings != null)
            {
                existing.RobloxSettings = account.RobloxSettings;
            }

            _accountStore?.Upsert(existing);
            existingItem.Refresh();
            SelectedAccountItem = existingItem;
            SetLocalizedStatus("Str_Accounts_StatusAdded", existing.Username);
            AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_StatusAdded", existing.Username));
            return;
        }

        _accountStore?.Upsert(account);
        if (_accountStore != null)
        {
            ReloadAccounts();
            SelectedAccountItem = AccountItems.FirstOrDefault(a =>
                (account.UserId > 0 && a.UserId == account.UserId) || a.Id == account.Id);
        }
        else
        {
            Accounts.Add(account);
            var newItem = new AccountItem(account);
            AccountItems.Add(newItem);
            SelectedAccountItem = newItem;
            FilteredAccountItems.Refresh();
            OnPropertyChanged(nameof(AccountCountText));
        }

        SetLocalizedStatus("Str_Accounts_StatusAdded", account.Username);
        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_StatusAdded", account.Username));
    }

    public void RemoveSelectedAccount()
    {
        if (_selectedAccount == null) return;
        _accountStore?.Delete(_selectedAccount.Id);
        string name = _selectedAccount.Username;
        if (_accountStore != null)
        {
            ReloadAccounts();
        }
        else
        {
            var item = AccountItems.FirstOrDefault(i => i.Id == _selectedAccount.Id);
            if (item != null) AccountItems.Remove(item);
            Accounts.Remove(_selectedAccount);
            SelectedAccountItem = null;
            OnPropertyChanged(nameof(AccountCountText));
        }
        SetLocalizedStatus("Str_Accounts_StatusRemoved", name);
    }

    public int RemoveCheckedOrSelectedAccounts()
    {
        if (IsLaunching) return 0;

        var toRemove = AccountItems.Where(a => a.IsChecked).ToList();
        if (toRemove.Count == 0 && SelectedAccountItem != null)
        {
            toRemove.Add(SelectedAccountItem);
        }

        if (toRemove.Count == 0)
        {
            SetLocalizedStatus("Str_Accounts_StatusSelectToRemove");
            return 0;
        }

        int removedCount = 0;
        foreach (var item in toRemove)
        {
            if (_accountStore == null || _accountStore.Delete(item.Id))
            {
                removedCount++;
            }

            Accounts.Remove(item.Account);
            AccountItems.Remove(item);
        }

        if (SelectedAccountItem != null && toRemove.Contains(SelectedAccountItem))
        {
            SelectedAccountItem = null;
        }

        FilteredAccountItems.Refresh();
        OnPropertyChanged(nameof(AccountCountText));

        SetLocalizedStatus("Str_Accounts_StatusRemovedCountFormat", removedCount);
        AppendActivityLog(LocalizationService.Instance.Format(
            "Str_Accounts_LogRemovedCountFormat",
            removedCount,
            string.Join(", ", toRemove.Select(a => a.Username))));
        return removedCount;
    }

    public void ToggleSelectAllAccounts()
    {
        if (AccountItems.Count == 0) return;

        bool allChecked = AccountItems.All(a => a.IsChecked);
        foreach (var item in AccountItems)
        {
            item.IsChecked = !allChecked;
        }
    }

    public bool MoveSelectedAccount(int delta)
    {
        if (IsLaunching || SelectedAccountItem is not AccountItem selected) return false;

        int index = AccountItems.IndexOf(selected);
        int targetIndex = index + delta;
        if (index < 0 || targetIndex < 0 || targetIndex >= AccountItems.Count) return false;

        AccountItems.Move(index, targetIndex);
        int accIndex = Accounts.IndexOf(selected.Account);
        if (accIndex >= 0 && accIndex != targetIndex && targetIndex < Accounts.Count)
        {
            Accounts.Move(accIndex, targetIndex);
        }
        else if (accIndex >= 0)
        {
            Accounts.Move(accIndex, Math.Clamp(targetIndex, 0, Accounts.Count - 1));
        }

        SelectedAccountItem = selected;
        FilteredAccountItems.Refresh();
        _accountStore?.SaveAll(AccountItems.Select(a => a.Account));
        return true;
    }

    public void CommitAccountNotesEdit(AccountItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            _accountStore?.Upsert(item.Account);
            SetLocalizedStatus("Str_Accounts_StatusUpdatedNotesFormat", item.Username);
        }
        catch (Exception ex)
        {
            AppendActivityLog($"Notes save failed: {ex.Message}");
        }
    }

    public int ApplyBatchAfkPreset()
    {
        if (IsLaunching) return 0;

        var targets = AccountItems.Where(a => a.IsChecked).ToList();
        if (targets.Count == 0 && SelectedAccountItem is AccountItem selected)
        {
            targets.Add(selected);
        }

        if (targets.Count == 0)
        {
            SetLocalizedStatus("Str_Accounts_StatusSelectForAfk");
            return 0;
        }

        foreach (var item in targets)
        {
            item.Account.RobloxSettings = RobloxClientSettingsProfile.CreateAfkLowPerfPreset();
            _accountStore?.Upsert(item.Account);
            item.Refresh();
        }

        SetLocalizedStatus("Str_Accounts_StatusBatchAfkAppliedFormat", targets.Count);
        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogBatchAfkAppliedFormat", targets.Count));
        return targets.Count;
    }

    public void UpdateAccountSettings(AccountItem item, RobloxClientSettingsProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.Account.RobloxSettings = profile is { HasOverrides: true }
            ? profile.Clone()
            : null;
        _accountStore?.Upsert(item.Account);
        item.Refresh();

        SetLocalizedStatus("Str_Accounts_StatusSavedAccountSettingsFormat", item.Username, item.SettingsSummary);
        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogSavedAccountSettingsFormat", item.Username, item.SettingsSummary));
    }

    public bool SaveCurrentJoinTarget()
    {
        string name = TargetName.Trim();
        string targetInput = LaunchTargetInput.Trim();
        string? jobId = string.IsNullOrWhiteSpace(LaunchJobId) ? null : LaunchJobId.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            SetLocalizedStatus("Str_Accounts_StatusSelectTargetName");
            return false;
        }

        string savedTargetInput;
        string? savedJobId;
        try
        {
            if (_isFollowUserTargetMode)
            {
                savedTargetInput = RobloxLauncher.BuildEffectiveTargetInput(targetInput, null, followUserMode: true);
                savedJobId = null;
            }
            else
            {
                var parsed = RobloxLauncher.ParseTargetInput(targetInput, followUserMode: false);
                savedTargetInput = targetInput;
                savedJobId = parsed.Kind == LaunchTargetKind.FollowUser ? null : jobId;
            }
        }
        catch (Exception ex)
        {
            SetLocalizedStatus("Str_Accounts_StatusInvalidTarget");
            AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogSaveTargetFailedFormat", ex.Message));
            return false;
        }

        var existing = SavedTargets.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        SavedJoinTarget saved;
        if (existing != null)
        {
            existing.Name = name;
            existing.TargetInput = savedTargetInput;
            existing.JobId = savedJobId;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            saved = existing;
            int idx = SavedTargets.IndexOf(existing);
            if (idx >= 0)
            {
                SavedTargets[idx] = existing;
            }
        }
        else
        {
            saved = new SavedJoinTarget
            {
                Name = name,
                TargetInput = savedTargetInput,
                JobId = savedJobId,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            SavedTargets.Add(saved);
        }

        _joinTargetStore.Upsert(saved);
        SelectedSavedTarget = saved;

        SetLocalizedStatus("Str_Accounts_StatusSavedTargetFormat", saved.Name);
        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogSavedTargetFormat", saved.Name, saved.TargetInput));
        return true;
    }

    public bool DeleteSelectedJoinTarget()
    {
        if (SelectedSavedTarget is not SavedJoinTarget selected)
        {
            SetLocalizedStatus("Str_Accounts_StatusSelectTargetToDelete");
            return false;
        }

        _joinTargetStore.Delete(selected.Id);
        string deletedName = selected.Name;
        SavedTargets.Remove(selected);
        SelectedSavedTarget = null;

        SetLocalizedStatus("Str_Accounts_StatusDeletedTargetFormat", deletedName);
        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogDeletedTargetFormat", deletedName));
        return true;
    }

    public (int ImportedAccounts, int ImportedTargets) ImportFromMultiRoblox(string? multiRobloxDataDir = null)
    {
        string sourceDir = string.IsNullOrWhiteSpace(multiRobloxDataDir)
            ? Path.Combine(_paths.LocalAppData, "MultiRoblox")
            : multiRobloxDataDir;

        int importedAccounts = 0;
        int importedTargets = 0;

        try
        {
            string accountsPath = Path.Combine(sourceDir, "accounts.dat");
            if (File.Exists(accountsPath))
            {
                var sourceStore = new DpapiAccountStore(accountsPath);
                var existingUserIds = AccountItems.Where(a => a.UserId > 0).Select(a => a.UserId).ToHashSet();
                var existingIds = AccountItems.Select(a => a.Id).ToHashSet();

                foreach (var src in sourceStore.LoadAll())
                {
                    if ((src.UserId > 0 && existingUserIds.Contains(src.UserId)) || existingIds.Contains(src.Id))
                    {
                        continue;
                    }

                    var imported = new RobloxAccount
                    {
                        Id = src.Id,
                        Username = src.Username,
                        DisplayName = string.IsNullOrWhiteSpace(src.DisplayName) ? src.Username : src.DisplayName,
                        UserId = src.UserId,
                        Cookie = src.Cookie,
                        Notes = src.Notes,
                        CreatedAt = src.CreatedAt,
                        LastLaunchedAt = src.LastLaunchedAt,
                        RobloxSettings = src.RobloxSettings?.Clone()
                    };

                    _accountStore?.Upsert(imported);
                    Accounts.Add(imported);
                    AccountItems.Add(new AccountItem(imported));
                    if (imported.UserId > 0) existingUserIds.Add(imported.UserId);
                    existingIds.Add(imported.Id);
                    importedAccounts++;
                }

                if (importedAccounts > 0)
                {
                    FilteredAccountItems.Refresh();
                    OnPropertyChanged(nameof(AccountCountText));
                }
            }

            string targetsPath = Path.Combine(sourceDir, "targets.json");
            if (File.Exists(targetsPath))
            {
                var sourceTargets = new JoinTargetStore(targetsPath).LoadAll();
                var existingNames = SavedTargets.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var existingIds = SavedTargets.Select(t => t.Id).ToHashSet();

                foreach (var target in sourceTargets)
                {
                    if (existingNames.Contains(target.Name) || existingIds.Contains(target.Id))
                    {
                        continue;
                    }

                    var importedTarget = new SavedJoinTarget
                    {
                        Id = target.Id,
                        Name = target.Name,
                        TargetInput = target.TargetInput,
                        JobId = target.JobId,
                        UpdatedAt = target.UpdatedAt
                    };
                    _joinTargetStore.Upsert(importedTarget);
                    SavedTargets.Add(importedTarget);
                    existingNames.Add(importedTarget.Name);
                    existingIds.Add(importedTarget.Id);
                    importedTargets++;
                }
            }

            if (importedAccounts > 0 || importedTargets > 0)
            {
                SetLocalizedStatus("Str_Accounts_StatusImportSuccessFormat", importedAccounts, importedTargets);
                AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_StatusImportSuccessFormat", importedAccounts, importedTargets));
            }
            else
            {
                SetLocalizedStatus("Str_Accounts_StatusImportNone");
            }
        }
        catch (Exception ex)
        {
            SetLocalizedStatus("Str_Accounts_StatusImportNone");
            AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogImportFailedFormat", ex.Message));
        }

        return (importedAccounts, importedTargets);
    }

    public async Task<LaunchResult?> LaunchSelectedAccountAsync(CancellationToken ct = default)
    {
        if (_launcher == null || IsLaunching) return null;

        AccountItem? targetItem = SelectedAccountItem
            ?? (_selectedAccount != null
                ? AccountItems.FirstOrDefault(i => ReferenceEquals(i.Account, _selectedAccount) || i.Id == _selectedAccount.Id)
                  ?? new AccountItem(_selectedAccount)
                : null);

        if (targetItem == null)
        {
            var settings = new LaunchSettings();
            return await _launcher.LaunchPlayerAsync(settings, ct);
        }

        var batchResult = await LaunchAccountsSequentiallyCoreAsync([targetItem], ct);
        return batchResult.LastResult;
    }

    public async Task<LaunchResult?> LaunchSelectedFromUiAsync(CancellationToken ct = default)
    {
        if (IsLaunching) return null;

        if (SelectedAccountItem is not AccountItem selected)
        {
            SetLocalizedStatus("Str_Accounts_StatusSelectToLaunch");
            AppendActivityLog(LocalizationService.Instance.GetString("Str_Accounts_LogCancelledNoSelection"));
            return null;
        }

        var batchResult = await LaunchAccountsSequentiallyCoreAsync([selected], ct);
        return batchResult.LastResult;
    }

    public async Task<int> LaunchCheckedAccountsAsync(CancellationToken ct = default)
    {
        if (IsLaunching) return 0;

        var checkedAccounts = AccountItems.Where(a => a.IsChecked).ToList();
        if (checkedAccounts.Count == 0)
        {
            SetLocalizedStatus("Str_Accounts_StatusCheckToLaunch");
            AppendActivityLog(LocalizationService.Instance.GetString("Str_Accounts_LogCancelledNoChecked"));
            return 0;
        }

        var batchResult = await LaunchAccountsSequentiallyCoreAsync(checkedAccounts, ct);
        return batchResult.SuccessCount;
    }

    public async Task<int> LaunchAccountsSequentiallyAsync(IReadOnlyList<AccountItem> targets, CancellationToken ct = default)
    {
        var result = await LaunchAccountsSequentiallyCoreAsync(targets, ct).ConfigureAwait(false);
        return result.SuccessCount;
    }

    private async Task<(int SuccessCount, LaunchResult? LastResult)> LaunchAccountsSequentiallyCoreAsync(
        IReadOnlyList<AccountItem> targets,
        CancellationToken externalCt)
    {
        if (_launcher == null || targets.Count == 0 || IsLaunching)
        {
            return (0, null);
        }

        string effectiveTargetInput;
        LaunchTarget parsedTarget;
        try
        {
            effectiveTargetInput = ResolveEffectiveTargetInput();
            parsedTarget = RobloxLauncher.ParseTargetInput(effectiveTargetInput, _isFollowUserTargetMode);
        }
        catch (Exception ex)
        {
            SetLocalizedStatus("Str_Accounts_StatusInvalidLaunchTarget");
            AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogInvalidLaunchTargetFormat", ex.Message));
            return (0, new LaunchResult(false, string.Empty, new LaunchTarget(LaunchTargetKind.Standard, null), DateTimeOffset.UtcNow, null, ex.Message));
        }

        if (_settings.MultiInstanceEnabled && _multiInstanceController != null)
        {
            var gateResult = LaunchGate.VerifyLaunchReadiness(_multiInstanceController, true);
            if (!gateResult.CanLaunch)
            {
                string reason = gateResult.WarningMessage ?? LaunchGate.BuildDiagnosticBlockedMessage(gateResult.Status);
                SetLocalizedStatus("Str_Accounts_StatusBlockedNoMutex");
                AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogBlockedNoMutexFormat", reason));
                RefreshMultiInstanceStatus();
                return (0, new LaunchResult(false, string.Empty, parsedTarget, DateTimeOffset.UtcNow, null, reason));
            }
        }

        _launchCts?.Dispose();
        _launchCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        OnPropertyChanged(nameof(CanCancelLaunch));
        var ct = _launchCts.Token;

        bool hasAnySettingsOverride = targets.Any(t => t.Account.RobloxSettings?.HasOverrides == true);
        string? settingsBackup = null;
        if (hasAnySettingsOverride)
        {
            try
            {
                settingsBackup = _globalSettingsManager.CaptureBackup();
                if (settingsBackup != null)
                {
                    AppendActivityLog(LocalizationService.Instance.GetString("Str_Accounts_LogSettingsBackedUp"));
                }
            }
            catch (Exception ex)
            {
                AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogSettingsBackupFailedFormat", ex.Message));
            }
        }

        IsLaunching = true;
        SetLocalizedStatus("Str_Accounts_StatusBatchLaunchingFormat", targets.Count);

        int successCount = 0;
        LaunchResult? lastResult = null;

        try
        {
            if (_launcher is BootstrapperOrchestrator orchestrator)
            {
                SetLocalizedStatus("Str_Accounts_StatusPreparingBatch");
                await orchestrator.PrepareForBatchLaunchAsync(ct);
            }

            for (int i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var item = targets[i];
                SetLocalizedStatus("Str_Accounts_StatusLaunchingItemFormat", item.Username, i + 1, targets.Count);
                AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_StatusLaunchingItemFormat", item.Username, i + 1, targets.Count));

                if (settingsBackup != null && i > 0)
                {
                    try
                    {
                        _globalSettingsManager.RestoreBackup(settingsBackup);
                    }
                    catch (Exception ex)
                    {
                        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogSettingsRestoreFailedFormat", ex.Message));
                    }
                }

                if (item.Account.RobloxSettings?.HasOverrides == true)
                {
                    try
                    {
                        _globalSettingsManager.ApplyProfile(item.Account.RobloxSettings);
                        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogAppliedSettingsFormat", item.Username, item.SettingsSummary));
                    }
                    catch (Exception ex)
                    {
                        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogApplySettingsFailedFormat", item.Username, ex.Message));
                    }
                }

                var previousPids = _processSupervisor?.CaptureRunningClientPids() ?? new HashSet<int>();
                bool launchSucceeded = false;

                try
                {
                    LaunchResult result = _launcher is BootstrapperOrchestrator orch
                        ? await orch.LaunchAccountAsync(item.Account, parsedTarget, skipDeploymentCheck: true, ct)
                        : await _launcher.LaunchAccountAsync(item.Account, parsedTarget, ct);

                    lastResult = result;
                    if (result.Success)
                    {
                        _accountStore?.Upsert(item.Account);
                        item.Refresh();
                        successCount++;
                        launchSucceeded = true;

                        AppendActivityLog(LocalizationService.Instance.Format(
                            "Str_Accounts_LogLaunchedItemFormat",
                            item.Username,
                            result.Target.PlaceId?.ToString() ?? (result.Target.UserId?.ToString() ?? "-"),
                            result.Target.Kind));
                    }
                    else
                    {
                        AppendActivityLog(LocalizationService.Instance.Format(
                            "Str_Accounts_LogLaunchItemFailedFormat",
                            item.Username,
                            result.ErrorMessage ?? LocalizationService.Instance.GetString("Str_Dialog_LaunchFailedFallback")));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (RobloxApiException ex)
                {
                    string detail = $"{ex.StatusCode}: {ex.Message}";
                    lastResult = new LaunchResult(false, string.Empty, parsedTarget, DateTimeOffset.UtcNow, null, ex.Message);
                    AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogLaunchItemFailedFormat", item.Username, detail));
                }
                catch (Exception ex)
                {
                    lastResult = new LaunchResult(false, string.Empty, parsedTarget, DateTimeOffset.UtcNow, null, ex.Message);
                    AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogLaunchItemFailedFormat", item.Username, ex.Message));
                }

                bool shouldWaitForWindow = launchSucceeded && _processSupervisor != null && (i < targets.Count - 1 || settingsBackup != null);
                if (shouldWaitForWindow)
                {
                    int timeoutSeconds = Math.Max(1, (int)Math.Ceiling(WindowWaitTimeout.TotalSeconds));
                    SetLocalizedStatus("Str_Accounts_StatusWaitingWindowFormat", item.Username, timeoutSeconds);
                    AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogWaitingWindowFormat", item.Username));

                    var waitResult = await _processSupervisor!.WaitForNewClientMainWindowAsync(
                        previousPids,
                        WindowWaitTimeout,
                        ProcessSupervisor.DefaultWindowPollInterval,
                        ct);

                    if (waitResult.WindowReady)
                    {
                        AppendActivityLog(i < targets.Count - 1
                            ? LocalizationService.Instance.Format("Str_Accounts_LogWindowReadyNextFormat", item.Username, waitResult.ProcessId)
                            : LocalizationService.Instance.Format("Str_Accounts_LogWindowReadySingleFormat", item.Username, waitResult.ProcessId));
                    }
                    else if (waitResult.TimedOut)
                    {
                        AppendActivityLog(i < targets.Count - 1
                            ? LocalizationService.Instance.Format("Str_Accounts_LogWindowTimeoutNextFormat", item.Username, timeoutSeconds)
                            : LocalizationService.Instance.Format("Str_Accounts_LogWindowTimeoutSingleFormat", item.Username, timeoutSeconds));
                    }
                }
            }

            SetLocalizedStatus("Str_Accounts_StatusBatchCompletedFormat", successCount, targets.Count);
        }
        catch (OperationCanceledException)
        {
            SetLocalizedStatus("Str_Accounts_StatusBatchCancelledFormat", successCount, targets.Count);
            AppendActivityLog(LocalizationService.Instance.GetString("Str_Accounts_LogBatchCancelled"));
        }
        finally
        {
            if (settingsBackup != null)
            {
                try
                {
                    _globalSettingsManager.RestoreBackup(settingsBackup);
                    AppendActivityLog(LocalizationService.Instance.GetString("Str_Accounts_LogSettingsRestored"));
                }
                catch (Exception ex)
                {
                    AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogSettingsRestoreFailedFormat", ex.Message));
                }
            }

            _launchCts?.Dispose();
            _launchCts = null;
            IsLaunching = false;
            RefreshMultiInstanceStatus();
            RefreshProtocolStatus();
        }

        return (successCount, lastResult);
    }

    private string ResolveEffectiveTargetInput()
    {
        if (!string.IsNullOrWhiteSpace(_launchTargetInput))
        {
            return RobloxLauncher.BuildEffectiveTargetInput(_launchTargetInput, _launchJobId, _isFollowUserTargetMode);
        }

        if (!string.IsNullOrWhiteSpace(_launchShareLink))
        {
            string share = _launchShareLink.Trim();
            string normalizedShare = share.Contains("/share", StringComparison.OrdinalIgnoreCase) || share.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? share
                : $"https://www.roblox.com/share?code={Uri.EscapeDataString(share)}&type=Server";
            return RobloxLauncher.BuildEffectiveTargetInput(normalizedShare, null);
        }

        if (!string.IsNullOrWhiteSpace(_launchPlaceIdText))
        {
            return RobloxLauncher.BuildEffectiveTargetInput(_launchPlaceIdText, _launchJobId);
        }

        return RobloxLauncher.BuildEffectiveTargetInput(string.Empty, _launchJobId, _isFollowUserTargetMode);
    }

    public void CancelActiveLaunch()
    {
        if (_launchCts == null) return;

        SetLocalizedStatus("Str_Accounts_StatusCancelling");
        try
        {
            _launchCts.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    public void ClearActivityLog()
    {
        _activityLogBuilder.Clear();
        OnPropertyChanged(nameof(ActivityLogText));
    }

    public void AppendActivityLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _activityLogBuilder.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        OnPropertyChanged(nameof(ActivityLogText));
        ActivityLogAppended?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatMultiInstanceStatusLog(string prefix, MultiInstanceStatus status)
    {
        string pidsText = status.RunningRobloxPids.Count > 0
            ? string.Join(", ", status.RunningRobloxPids)
            : "none";
        string errorPart = string.IsNullOrWhiteSpace(status.LastError)
            ? string.Empty
            : $", LastError={status.LastError}";
        return LocalizationService.Instance.Format(
            "Str_Accounts_LogEnabledFormat",
            prefix,
            status.OwnsMutex,
            status.WaitingForMutex,
            pidsText,
            errorPart);
    }

    public FastFlagEditorViewModel CreateFastFlagEditorViewModel() => new(_fastFlagManager);

    public void LoadFastFlagPresets()
    {
        _fastFlagManager.Load();
        if (_fastFlagManager.GetFlag("FFlagDebugGraphicsPreferVulkan")?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true)
            _selectedRenderingMode = RenderingMode.Vulkan;
        else if (_fastFlagManager.GetFlag("FFlagDebugGraphicsPreferD3D11")?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true)
            _selectedRenderingMode = RenderingMode.D3D11;
        else
            _selectedRenderingMode = RenderingMode.Default;

        if (int.TryParse(_fastFlagManager.GetFlag("FIntDebugForceMSAASamples")?.ToString(), out int msaa))
        {
            _selectedMSAAMode = msaa switch
            {
                1 => MSAAMode.x1,
                2 => MSAAMode.x2,
                4 => MSAAMode.x4,
                _ => MSAAMode.Default
            };
        }

        if (int.TryParse(_fastFlagManager.GetFlag("DFIntDebugFRMQualityLevelOverride")?.ToString(), out int frm))
            _frmQualityOverride = Math.Clamp(frm, 1, 21);

        if (int.TryParse(_fastFlagManager.GetFlag("DFIntCSGLevelOfDetailSwitchingDistanceStatic")?.ToString(), out int lod))
            _meshLodLevel = Math.Clamp(lod, 0, 3);

        _disableDpiScaling = _fastFlagManager.GetFlag("DFFlagDisableDPIScale")?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true;
        _manualFullscreen = _fastFlagManager.GetFlag("FFlagHandleAltEnterFullscreenManually")?.ToString()?.Equals("False", StringComparison.OrdinalIgnoreCase) == true;
    }

    public void ApplyFastFlagPresets()
    {
        if (!_settings.UseFastFlagManager) return;

        switch (_selectedRenderingMode)
        {
            case RenderingMode.Vulkan:
                _fastFlagManager.ApplyPreset("Rendering.Mode.Vulkan", "True");
                break;
            case RenderingMode.D3D11:
                _fastFlagManager.ApplyPreset("Rendering.Mode.D3D11", "True");
                break;
            default:
                _fastFlagManager.RemoveFlag("FFlagDebugGraphicsPreferVulkan");
                _fastFlagManager.RemoveFlag("FFlagDebugGraphicsPreferD3D11");
                break;
        }

        switch (_selectedMSAAMode)
        {
            case MSAAMode.x1: _fastFlagManager.ApplyPreset("Rendering.MSAA", 1); break;
            case MSAAMode.x2: _fastFlagManager.ApplyPreset("Rendering.MSAA", 2); break;
            case MSAAMode.x4: _fastFlagManager.ApplyPreset("Rendering.MSAA", 4); break;
            default: _fastFlagManager.RemoveFlag("FIntDebugForceMSAASamples"); break;
        }

        _fastFlagManager.ApplyPreset("Rendering.FRMQualityOverride", _frmQualityOverride);
        _fastFlagManager.ApplyPreset("Geometry.MeshLOD", _meshLodLevel);
        _fastFlagManager.ApplyPreset("Rendering.DisableScaling", _disableDpiScaling ? "True" : "False");
        _fastFlagManager.ApplyPreset("Rendering.ManualFullscreen", _manualFullscreen ? "False" : "True");
        _fastFlagManager.Save();
    }

    public bool ApplyCustomFont(string filePath)
    {
        var validator = new FontValidator();
        if (!validator.ValidateFont(filePath, out _, out var error))
        {
            _fontStatusKey = "Str_Mods_InvalidFontFormat";
            _fontStatusArgs = [error];
            CurrentFontStatus = FormatLocalized(_fontStatusKey, _fontStatusArgs);
            SetLocalizedStatus(_fontStatusKey, _fontStatusArgs);
            return false;
        }

        string dest = Path.Combine(_paths.Modifications, @"content\fonts\CustomFont.ttf");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(filePath, dest, overwrite: true);
        RefreshModStatus();
        SetLocalizedStatus("Str_Mods_StatusAppliedFont");
        return true;
    }

    public void ResetCustomFont()
    {
        string dest = Path.Combine(_paths.Modifications, @"content\fonts\CustomFont.ttf");
        if (File.Exists(dest)) File.Delete(dest);
        RefreshModStatus();
        SetLocalizedStatus("Str_Mods_StatusResetFont");
    }

    public void ApplyCursorPreset(CursorPreset preset)
    {
        CursorPresetHandler.ApplyCursorPreset(_paths.Modifications, preset);
        RefreshModStatus();
        SetLocalizedStatus("Str_Mods_StatusAppliedCursorFormat", preset);
    }

    public bool ApplyCustomDeathSound(string filePath)
    {
        try
        {
            DeathSoundPresetHandler.ApplyDeathSoundFile(filePath, _paths.Modifications);
            RefreshModStatus();
            SetLocalizedStatus("Str_Mods_StatusAppliedSound");
            return true;
        }
        catch (Exception ex)
        {
            SetLocalizedStatus("Str_Mods_InvalidAudioFormat", ex.Message);
            return false;
        }
    }

    public void ResetDeathSound()
    {
        DeathSoundPresetHandler.ResetDeathSound(_paths.Modifications);
        RefreshModStatus();
        SetLocalizedStatus("Str_Mods_StatusResetSound");
    }

    public void RefreshModStatus()
    {
        string fontPath = Path.Combine(_paths.Modifications, @"content\fonts\CustomFont.ttf");
        _fontStatusKey = File.Exists(fontPath) ? "Str_Mods_CustomFontActive" : "Str_Mods_DefaultFontStatus";
        _fontStatusArgs = Array.Empty<object?>();
        CurrentFontStatus = FormatLocalized(_fontStatusKey, _fontStatusArgs);

        string ouchPath = Path.Combine(_paths.Modifications, DeathSoundPresetHandler.RelativeOuchPath);
        HasCustomDeathSound = File.Exists(ouchPath);
        OnPropertyChanged(nameof(CustomDeathSoundStatusText));

        string mobileRbxl = Path.Combine(_paths.Modifications, AvatarBackgroundPresetHandler.RelativeMobileRbxlPath);
        _oldAvatarBackgroundEnabled = File.Exists(mobileRbxl);
        OnPropertyChanged(nameof(OldAvatarBackgroundEnabled));

        ActiveModFileCount = Directory.Exists(_paths.Modifications)
            ? Directory.GetFiles(_paths.Modifications, "*", SearchOption.AllDirectories).Length
            : 0;
    }

    public void RefreshCustomThemes()
    {
        AvailableCustomThemes.Clear();
        foreach (var theme in XmlThemeParser.ListAvailableThemes(_paths.CustomThemes))
        {
            AvailableCustomThemes.Add(theme);
        }
        UpdatePreviewTheme();
    }

    private void UpdatePreviewTheme()
    {
        string? path = XmlThemeParser.ResolveThemeFilePath(_paths.CustomThemes, _settings.SelectedCustomTheme);
        PreviewTheme = path != null ? XmlThemeParser.LoadFromFile(path) : XmlThemeParser.GetDefaultFluent();
    }

    public void RunCleanerNow()
    {
        var cleaner = new CacheCleaner(_paths);
        var policy = CleanerPolicyMapper.FromSettings(_settings);
        var report = cleaner.ExecuteCleanup(policy);
        double freedMb = report.BytesFreed / (1024.0 * 1024.0);
        _cleanerStatusKey = "Str_Behaviour_CleanerReportFormat";
        _cleanerStatusArgs = [report.FilesDeleted, freedMb];
        CleanerStatusText = FormatLocalized(_cleanerStatusKey, _cleanerStatusArgs);
        SetLocalizedStatus(_cleanerStatusKey, _cleanerStatusArgs);
    }

    public async Task TestFastestCdnMirrorAsync()
    {
        _cdnTestResultKey = "Str_Channel_TestingMirrors";
        _cdnTestResultArgs = Array.Empty<object?>();
        CdnTestResult = FormatLocalized(_cdnTestResultKey, _cdnTestResultArgs);
        try
        {
            var selector = new CdnMirrorSelector();
            var sw = Stopwatch.StartNew();
            string optimal = await selector.ResolveOptimalCdnUrlAsync();
            sw.Stop();
            _cdnTestResultKey = "Str_Channel_MirrorOptimalFormat";
            _cdnTestResultArgs = [optimal, sw.ElapsedMilliseconds];
            CdnTestResult = FormatLocalized(_cdnTestResultKey, _cdnTestResultArgs);
        }
        catch (Exception ex)
        {
            _cdnTestResultKey = "Str_Channel_MirrorFailedFormat";
            _cdnTestResultArgs = [ex.Message];
            CdnTestResult = FormatLocalized(_cdnTestResultKey, _cdnTestResultArgs);
        }
    }

    public void LoadGlobalSettingsSafe()
    {
        try
        {
            _globalSettingsManager.Load();
            OnPropertyChanged(nameof(FramerateCap));
            OnPropertyChanged(nameof(ReadOnlyLocked));
            OnPropertyChanged(nameof(MouseSensitivity));
            OnPropertyChanged(nameof(SavedQualityLevel));
            OnPropertyChanged(nameof(VREnabled));
            OnPropertyChanged(nameof(PreferredTransparency));
            OnPropertyChanged(nameof(ReducedMotion));
            OnPropertyChanged(nameof(PreferredTextSize));
        }
        catch { }
    }

    public void SaveGlobalSettings()
    {
        _globalSettingsManager.Save();
        SetLocalizedStatus("Str_GlobalSettings_StatusSaved");
    }

    public void CreateDesktopShortcut()
    {
        _installerService.CreateDesktopShortcut();
        SetLocalizedStatus("Str_Shortcuts_StatusDesktopCreated");
    }

    public void CreateStartMenuShortcuts()
    {
        _installerService.CreateStartMenuShortcuts();
        SetLocalizedStatus("Str_Shortcuts_StatusStartMenuCreated");
    }

    public void RegisterPlayerProtocols()
    {
        string exe = File.Exists(_paths.Application) ? _paths.Application : _paths.Process;
        WindowsRegistry.RegisterPlayer(exe);
        WindowsRegistry.RegisterAppLocation(exe, _paths.Base);
        WindowsRegistry.RegisterUninstall(exe);
        SetLocalizedStatus("Str_Shortcuts_StatusPlayerRegistered");
    }

    public void RegisterStudioProtocols()
    {
        string exe = File.Exists(_paths.Application) ? _paths.Application : _paths.Process;
        WindowsRegistry.RegisterStudio(exe);
        SetLocalizedStatus("Str_Shortcuts_StatusStudioRegistered");
    }

    public void UnregisterAllProtocols()
    {
        WindowsRegistry.UnregisterAll();
        SetLocalizedStatus("Str_Shortcuts_StatusUnregistered");
    }

    public void RemoveRobloxStartup()
    {
        WindowsRegistry.RemoveRobloxStartupEntry();
        SetLocalizedStatus("Str_Shortcuts_StatusStartupRemoved");
    }

    private bool HasCleanerDir(string key) =>
        _settings.CleanerDirectories.Contains(key, StringComparer.OrdinalIgnoreCase);

    private void SetCleanerDir(string key, bool enabled)
    {
        bool exists = HasCleanerDir(key);
        if (enabled && !exists)
            _settings.CleanerDirectories.Add(key);
        else if (!enabled && exists)
            _settings.CleanerDirectories.RemoveAll(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged($"Clean{key}");
    }
}
