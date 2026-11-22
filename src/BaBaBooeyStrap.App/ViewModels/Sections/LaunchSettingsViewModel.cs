using System.Diagnostics;
using System.IO;
using System.Reflection;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;

namespace BaBaBooeyStrap.App.ViewModels.Sections;

public sealed class LaunchSettingsViewModel : ViewModelBase
{
    private readonly Settings _settings;
    private readonly IPaths _paths;
    private readonly IGlobalSettingsManager _globalSettingsManager;
    private readonly IJsonManager<State>? _stateManager;
    private readonly IJsonManager<RobloxState>? _robloxStateManager;
    private readonly InstallerService _installerService;
    private readonly IGlobalSettingsGuard? _globalSettingsGuard;
    private readonly Action<string, object?[]> _setLocalizedStatus;

    private string _cdnTestResult = LocalizationService.Instance.GetString("Str_Common_NotTestedYet");
    private string _cdnTestResultKey = "Str_Common_NotTestedYet";
    private object?[] _cdnTestResultArgs = Array.Empty<object?>();
    private string _appUpdateStatusText = string.Empty;
    private string? _appUpdateStatusKey;
    private object?[] _appUpdateStatusArgs = Array.Empty<object?>();
    private bool _isCheckingAppUpdate;
    private bool _forceReinstall;
    private bool _isTestingCdn;
    private bool _hasUnsavedGlobalSettings;
    private bool _isReloadingGlobalSettings;

    public LaunchSettingsViewModel(
        Settings settings,
        IPaths paths,
        IGlobalSettingsManager globalSettingsManager,
        IJsonManager<State>? stateManager,
        IJsonManager<RobloxState>? robloxStateManager,
        InstallerService installerService,
        Action<string, object?[]> setLocalizedStatus,
        IGlobalSettingsGuard? globalSettingsGuard = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _globalSettingsManager = globalSettingsManager ?? throw new ArgumentNullException(nameof(globalSettingsManager));
        _stateManager = stateManager;
        _robloxStateManager = robloxStateManager;
        _installerService = installerService ?? throw new ArgumentNullException(nameof(installerService));
        _setLocalizedStatus = setLocalizedStatus ?? throw new ArgumentNullException(nameof(setLocalizedStatus));
        _globalSettingsGuard = globalSettingsGuard;

        if (_stateManager != null)
        {
            _forceReinstall = _stateManager.Prop.ForceReinstall;
        }

        LoadGlobalSettingsSafe();
    }

    // --- Launcher Behaviour ---
    public bool CheckForUpdates { get => _settings.CheckForUpdates; set { _settings.CheckForUpdates = value; OnPropertyChanged(); } }
    public bool CheckAppUpdates
    {
        get => _settings.CheckAppUpdates;
        set
        {
            if (_settings.CheckAppUpdates != value)
            {
                _settings.CheckAppUpdates = value;
                OnPropertyChanged();
            }
        }
    }
    public bool RunOnStartup
    {
        get => _settings.RunOnStartup;
        set
        {
            if (_settings.RunOnStartup != value)
            {
                _settings.RunOnStartup = value;
                OnPropertyChanged();
            }
        }
    }
    public bool BackgroundUpdatesEnabled { get => _settings.BackgroundUpdatesEnabled; set { _settings.BackgroundUpdatesEnabled = value; OnPropertyChanged(); } }
    public bool ConfirmLaunches { get => _settings.ConfirmLaunches; set { _settings.ConfirmLaunches = value; OnPropertyChanged(); } }
    public bool ForceLocalData { get => _settings.ForceLocalData; set { _settings.ForceLocalData = value; OnPropertyChanged(); } }
    public bool StaticDirectory { get => _settings.StaticDirectory; set { _settings.StaticDirectory = value; OnPropertyChanged(); } }
    public bool WPFSoftwareRender { get => _settings.WPFSoftwareRender; set { _settings.WPFSoftwareRender = value; OnPropertyChanged(); } }
    public bool DeveloperMode { get => _settings.DeveloperMode; set { _settings.DeveloperMode = value; OnPropertyChanged(); } }
    public bool DebugDisableVersionPackageCleanup { get => _settings.DebugDisableVersionPackageCleanup; set { _settings.DebugDisableVersionPackageCleanup = value; OnPropertyChanged(); } }

    // --- Channel & CDN ---
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

    public bool IsTestingCdn
    {
        get => _isTestingCdn;
        private set
        {
            if (SetProperty(ref _isTestingCdn, value))
            {
                OnPropertyChanged(nameof(IsNotTestingCdn));
            }
        }
    }

    public bool IsNotTestingCdn => !IsTestingCdn;

    public bool HasUnsavedGlobalSettings
    {
        get => _hasUnsavedGlobalSettings;
        private set => SetProperty(ref _hasUnsavedGlobalSettings, value);
    }

    internal bool IsReloadingGlobalSettings => _isReloadingGlobalSettings;

    // --- Global Settings ---
    public int FramerateCap
    {
        get => _globalSettingsManager.FramerateCap;
        set
        {
            if (_globalSettingsManager.FramerateCap != value)
            {
                _globalSettingsManager.FramerateCap = value;
                OnPropertyChanged();
                if (!_isReloadingGlobalSettings) HasUnsavedGlobalSettings = true;
            }
        }
    }

    public bool ReadOnlyLocked
    {
        get => _globalSettingsManager.ReadOnlyLocked;
        set
        {
            if (_globalSettingsManager.ReadOnlyLocked != value)
            {
                _globalSettingsManager.ReadOnlyLocked = value;
                OnPropertyChanged();
                if (!_isReloadingGlobalSettings) HasUnsavedGlobalSettings = true;
            }
        }
    }

    public float MouseSensitivity
    {
        get => _globalSettingsManager.MouseSensitivity;
        set
        {
            if (Math.Abs(_globalSettingsManager.MouseSensitivity - value) > 0.0001f)
            {
                _globalSettingsManager.MouseSensitivity = value;
                OnPropertyChanged();
                if (!_isReloadingGlobalSettings) HasUnsavedGlobalSettings = true;
            }
        }
    }

    public int SavedQualityLevel
    {
        get => _globalSettingsManager.SavedQualityLevel;
        set
        {
            if (_globalSettingsManager.SavedQualityLevel != value)
            {
                _globalSettingsManager.SavedQualityLevel = value;
                OnPropertyChanged();
                if (!_isReloadingGlobalSettings) HasUnsavedGlobalSettings = true;
            }
        }
    }

    public bool VREnabled
    {
        get => _globalSettingsManager.VREnabled;
        set
        {
            if (_globalSettingsManager.VREnabled != value)
            {
                _globalSettingsManager.VREnabled = value;
                OnPropertyChanged();
                if (!_isReloadingGlobalSettings) HasUnsavedGlobalSettings = true;
            }
        }
    }

    public float PreferredTransparency
    {
        get => _globalSettingsManager.PreferredTransparency;
        set
        {
            if (Math.Abs(_globalSettingsManager.PreferredTransparency - value) > 0.0001f)
            {
                _globalSettingsManager.PreferredTransparency = value;
                OnPropertyChanged();
                if (!_isReloadingGlobalSettings) HasUnsavedGlobalSettings = true;
            }
        }
    }

    public bool ReducedMotion
    {
        get => _globalSettingsManager.ReducedMotion;
        set
        {
            if (_globalSettingsManager.ReducedMotion != value)
            {
                _globalSettingsManager.ReducedMotion = value;
                OnPropertyChanged();
                if (!_isReloadingGlobalSettings) HasUnsavedGlobalSettings = true;
            }
        }
    }

    public int PreferredTextSize
    {
        get => _globalSettingsManager.PreferredTextSize;
        set
        {
            if (_globalSettingsManager.PreferredTextSize != value)
            {
                _globalSettingsManager.PreferredTextSize = value;
                OnPropertyChanged();
                if (!_isReloadingGlobalSettings) HasUnsavedGlobalSettings = true;
            }
        }
    }

    // --- Shortcuts & Paths ---
    public string BasePath => _paths.Base;
    public string VersionsPath => _paths.Versions;
    public string LogsPath => _paths.Logs;

    // --- About ---
    public string AppVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString()
        ?? "1.0.0.0";

    public string RuntimeInfo => $".NET {Environment.Version} | {(Environment.Is64BitProcess ? "win-x64" : "win-x86")} | {Environment.OSVersion}";

    public string AppUpdateStatusText
    {
        get => _appUpdateStatusText;
        private set => SetProperty(ref _appUpdateStatusText, value);
    }

    public bool IsCheckingAppUpdate
    {
        get => _isCheckingAppUpdate;
        set
        {
            if (SetProperty(ref _isCheckingAppUpdate, value))
            {
                OnPropertyChanged(nameof(IsNotCheckingAppUpdate));
            }
        }
    }

    public bool IsNotCheckingAppUpdate => !IsCheckingAppUpdate;

    public void SetAppUpdateStatus(string key, params object?[] args)
    {
        _appUpdateStatusKey = key;
        _appUpdateStatusArgs = args ?? Array.Empty<object?>();
        AppUpdateStatusText = LocalizedFormatHelper.Format(_appUpdateStatusKey, _appUpdateStatusArgs);
    }

    public void ResetChannelDefaults()
    {
        Channel = "production";
        SelectedChannelChangeMode = ChannelChangeMode.Automatic;
        SelectedDistributorType = DistributorType.Global;
        SelectedWebEnvironment = WebEnvironment.Production;
        RobloxDomain = "roblox.com";
        Locale = "nil";
        ForceRobloxLanguage = false;
        _setLocalizedStatus("Str_Channel_StatusResetDefaults", Array.Empty<object?>());
    }

    public void SyncRunOnStartupRegistry()
    {
        string exe = File.Exists(_paths.Application) ? _paths.Application : _paths.Process;
        WindowsRegistry.SetAppStartup(exe, _settings.RunOnStartup);
    }

    public void RefreshFromSettings()
    {
        OnPropertyChanged(nameof(CheckForUpdates));
        OnPropertyChanged(nameof(CheckAppUpdates));
        OnPropertyChanged(nameof(RunOnStartup));
        OnPropertyChanged(nameof(BackgroundUpdatesEnabled));
        OnPropertyChanged(nameof(ConfirmLaunches));
        OnPropertyChanged(nameof(ForceLocalData));
        OnPropertyChanged(nameof(StaticDirectory));
        OnPropertyChanged(nameof(WPFSoftwareRender));
        OnPropertyChanged(nameof(DeveloperMode));
        OnPropertyChanged(nameof(DebugDisableVersionPackageCleanup));
        OnPropertyChanged(nameof(Channel));
        OnPropertyChanged(nameof(SelectedChannelChangeMode));
        OnPropertyChanged(nameof(SelectedDistributorType));
        OnPropertyChanged(nameof(SelectedWebEnvironment));
        OnPropertyChanged(nameof(RobloxDomain));
        OnPropertyChanged(nameof(Locale));
        OnPropertyChanged(nameof(ForceRobloxLanguage));
        OnPropertyChanged(nameof(SelectedRobloxTheme));
        OnPropertyChanged(nameof(EnableRobloxBackgroundApp));
    }

    public async Task TestFastestCdnMirrorAsync()
    {
        if (IsTestingCdn) return;

        IsTestingCdn = true;
        _cdnTestResultKey = "Str_Channel_TestingMirrors";
        _cdnTestResultArgs = Array.Empty<object?>();
        CdnTestResult = LocalizedFormatHelper.Format(_cdnTestResultKey, _cdnTestResultArgs);
        try
        {
            var selector = new CdnMirrorSelector();
            var sw = Stopwatch.StartNew();
            string optimal = await selector.ResolveOptimalCdnUrlAsync(forceRefresh: true);
            sw.Stop();
            _cdnTestResultKey = "Str_Channel_MirrorOptimalFormat";
            _cdnTestResultArgs = [optimal, sw.ElapsedMilliseconds];
            CdnTestResult = LocalizedFormatHelper.Format(_cdnTestResultKey, _cdnTestResultArgs);
        }
        catch (Exception ex)
        {
            _cdnTestResultKey = "Str_Channel_MirrorFailedFormat";
            _cdnTestResultArgs = [ex.Message];
            CdnTestResult = LocalizedFormatHelper.Format(_cdnTestResultKey, _cdnTestResultArgs);
        }
        finally
        {
            IsTestingCdn = false;
        }
    }

    public void LoadGlobalSettingsSafe()
    {
        _isReloadingGlobalSettings = true;
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
            HasUnsavedGlobalSettings = false;
        }
        catch { }
        finally
        {
            _isReloadingGlobalSettings = false;
        }
    }

    public void SaveGlobalSettings()
    {
        _globalSettingsManager.Save();
        _globalSettingsGuard?.UpdateOriginalContentFromCurrentFile();
        HasUnsavedGlobalSettings = false;
        _setLocalizedStatus("Str_GlobalSettings_StatusSaved", Array.Empty<object?>());
    }

    public void CreateDesktopShortcut()
    {
        _installerService.CreateDesktopShortcut();
        _setLocalizedStatus("Str_Shortcuts_StatusDesktopCreated", Array.Empty<object?>());
    }

    public void CreateStartMenuShortcuts()
    {
        _installerService.CreateStartMenuShortcuts();
        _setLocalizedStatus("Str_Shortcuts_StatusStartMenuCreated", Array.Empty<object?>());
    }

    public void RegisterPlayerProtocols()
    {
        _installerService.RegisterPlayerProtocols();
        _setLocalizedStatus("Str_Shortcuts_StatusPlayerRegistered", Array.Empty<object?>());
    }

    public void RegisterStudioProtocols()
    {
        _installerService.RegisterStudioProtocols();
        _setLocalizedStatus("Str_Shortcuts_StatusStudioRegistered", Array.Empty<object?>());
    }

    public void UnregisterAllProtocols()
    {
        WindowsRegistry.UnregisterAll();
        _setLocalizedStatus("Str_Shortcuts_StatusUnregistered", Array.Empty<object?>());
    }

    public void RemoveRobloxStartup()
    {
        WindowsRegistry.RemoveRobloxStartupEntry();
        _setLocalizedStatus("Str_Shortcuts_StatusStartupRemoved", Array.Empty<object?>());
    }

    public void RefreshLocalizedProperties()
    {
        CdnTestResult = LocalizedFormatHelper.Format(_cdnTestResultKey, _cdnTestResultArgs);
        if (!string.IsNullOrEmpty(_appUpdateStatusKey))
        {
            AppUpdateStatusText = LocalizedFormatHelper.Format(_appUpdateStatusKey, _appUpdateStatusArgs);
        }
        OnPropertyChanged(nameof(InstalledPlayerVersion));
    }
}
