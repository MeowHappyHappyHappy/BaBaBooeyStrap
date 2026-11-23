using System.Collections.ObjectModel;
using System.ComponentModel;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels.Sections;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Launch;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.MultiInstance.Abstractions;

namespace BaBaBooeyStrap.App.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly Settings _settings;
    private readonly IPaths _paths;
    private readonly FastFlagManager _fastFlagManager;
    private readonly IJoinTargetStore _joinTargetStore;
    private readonly IJsonManager<Settings>? _settingsManager;
    private readonly IJsonManager<State>? _stateManager;
    private readonly IJsonManager<RobloxState>? _robloxStateManager;
    private readonly IAppUpdateService _appUpdateService;
    private readonly WindowSwitcherCoordinator? _switcherCoordinator;
    private readonly EventHandler<string> _languageChangedHandler;

    private string _statusMessage = LocalizationService.Instance.GetString("Str_Common_Ready");
    private string _statusMessageKey = "Str_Common_Ready";
    private object?[] _statusMessageArgs = Array.Empty<object?>();
    private bool _hasUnsavedChanges;
    private bool _isSuppressingDirtyTracking;

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
        IJoinTargetStore? joinTargetStore = null,
        IJsonManager<Settings>? settingsManager = null,
        IAppUpdateService? appUpdateService = null,
        IGlobalSettingsGuard? globalSettingsGuard = null,
        WindowSwitcherCoordinator? switcherCoordinator = null)
    {
        _settings = settings ?? new Settings();
        _paths = paths ?? Core.Paths.Current;
        var resolvedSupervisor = processSupervisor ?? new ProcessSupervisor();
        var resolvedGlobalSettings = globalSettingsManager ?? new GlobalSettingsManager(_paths.RobloxGlobalSettings);
        _joinTargetStore = joinTargetStore ?? new JoinTargetStore(_paths.TargetsFile);
        _fastFlagManager = new FastFlagManager(_paths.GetClientSettingsPath());
        _settingsManager = settingsManager;
        _stateManager = stateManager;
        _robloxStateManager = robloxStateManager;
        var installerService = new InstallerService(_paths, stateManager);
        _appUpdateService = appUpdateService ?? new AppUpdateService(_paths);
        _switcherCoordinator = switcherCoordinator;

        LaunchSettings = new LaunchSettingsViewModel(
            _settings,
            _paths,
            resolvedGlobalSettings,
            stateManager,
            robloxStateManager,
            installerService,
            SetLocalizedStatus,
            globalSettingsGuard);

        FastFlags = new FastFlagsViewModel(_settings, _fastFlagManager);

        Mods = new ModsViewModel(_paths, SetLocalizedStatus);

        AccountManagement = new AccountsViewModel(
            _settings,
            _paths,
            accountStore,
            _joinTargetStore,
            multiInstanceController,
            resolvedSupervisor,
            launcher,
            resolvedGlobalSettings,
            stateManager,
            SetLocalizedStatus,
            globalSettingsGuard: globalSettingsGuard,
            switcherCoordinator: switcherCoordinator);

        WindowIntegrations = new WindowAndIntegrationsViewModel(_settings, _paths, SetLocalizedStatus);

        LaunchSettings.PropertyChanged += OnLaunchSettingsPropertyChanged;
        FastFlags.PropertyChanged += OnFastFlagsPropertyChanged;
        WindowIntegrations.PropertyChanged += OnWindowIntegrationsPropertyChanged;
        AccountManagement.PropertyChanged += OnAccountManagementPropertyChanged;

        _languageChangedHandler = (_, _) => RefreshLocalizedProperties();
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
    }

    public Settings Settings => _settings;
    internal IPaths AppPaths => _paths;
    internal IJsonManager<Settings>? SettingsManager => _settingsManager;
    internal IJsonManager<State>? StateManager => _stateManager;
    internal IJsonManager<RobloxState>? RobloxStateManager => _robloxStateManager;
    internal IAppUpdateService AppUpdateService => _appUpdateService;

    public LaunchSettingsViewModel LaunchSettings { get; }
    public FastFlagsViewModel FastFlags { get; }
    public ModsViewModel Mods { get; }
    public AccountsViewModel AccountManagement { get; }
    public WindowAndIntegrationsViewModel WindowIntegrations { get; }

    public ObservableCollection<string> NavigationTabs { get; } = new()
    {
        "Accounts", "FastFlags", "Mods", "Appearance", "Behaviour", "Channel", "Global Settings", "Shortcuts", "About"
    };

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }

    public void SaveSettings(IJsonManager<Settings> manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        FastFlags.ApplyFastFlagPresets();

        string? secondaryWarning = null;
        if (LaunchSettings.HasUnsavedGlobalSettings)
        {
            try
            {
                LaunchSettings.SaveGlobalSettings();
            }
            catch (Exception ex)
            {
                Logger.ExceptionStatic("MainWindowViewModel.SaveSettings.GlobalSettings", ex);
                secondaryWarning = ex.Message;
            }
        }

        manager.Save();
        try
        {
            LaunchSettings.SyncRunOnStartupRegistry();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("MainWindowViewModel.SaveSettings.StartupRegistry", ex);
            secondaryWarning ??= ex.Message;
        }

        if (_stateManager != null)
        {
            AccountManagement.PersistLastTargetState();
            _stateManager.Save();
        }

        HasUnsavedChanges = false;
        _switcherCoordinator?.ReloadSettings();
        if (!string.IsNullOrEmpty(secondaryWarning))
        {
            SetLocalizedStatus("Str_MainWindow_StatusSavedWithWarningFormat", secondaryWarning);
        }
        else
        {
            SetLocalizedStatus("Str_MainWindow_StatusSavedAt", DateTime.Now.ToString("HH:mm:ss"));
        }
    }

    public void DiscardChanges(IJsonManager<Settings>? manager = null)
    {
        var resolvedManager = manager ?? _settingsManager;

        _isSuppressingDirtyTracking = true;
        try
        {
            if (resolvedManager != null)
            {
                ProfileTransferService.CopySettingsProperties(resolvedManager.OriginalProp, _settings);
            }

            LaunchSettings.LoadGlobalSettingsSafe();
            LaunchSettings.RefreshFromSettings();
            FastFlags.LoadFastFlagPresets();
            WindowIntegrations.RefreshFromSettings();
            AccountManagement.RefreshMultiInstanceStatus();
            HasUnsavedChanges = false;
        }
        finally
        {
            _isSuppressingDirtyTracking = false;
        }

        SetLocalizedStatus("Str_Common_Ready");
    }

    public void ExportProfileToFile(string filePath)
    {
        ProfileTransferService.ExportToFile(
            filePath,
            _settings,
            _fastFlagManager,
            _joinTargetStore,
            LaunchSettings.AppVersion);
        SetLocalizedStatus("Str_Behaviour_StatusProfileExported");
    }

    public ProfileBundle ImportProfileFromFile(
        string filePath,
        IJsonManager<Settings>? settingsManager = null,
        bool replaceSavedTargets = false)
    {
        var bundle = ProfileTransferService.ImportFromFile(filePath);
        ApplyImportedProfile(bundle, settingsManager ?? _settingsManager, replaceSavedTargets);
        return bundle;
    }

    public void ApplyImportedProfile(
        ProfileBundle bundle,
        IJsonManager<Settings>? settingsManager = null,
        bool replaceSavedTargets = false)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        _isSuppressingDirtyTracking = true;
        try
        {
            ProfileTransferService.ApplyImportedProfile(
                bundle,
                _settings,
                _fastFlagManager,
                _joinTargetStore,
                replaceSavedTargets);

            (settingsManager ?? _settingsManager)?.Save();
            try { LaunchSettings.SyncRunOnStartupRegistry(); } catch { }

            LaunchSettings.RefreshFromSettings();
            FastFlags.RefreshFromSettings();
            WindowIntegrations.RefreshFromSettings();
            AccountManagement.ReloadSavedTargets();
            HasUnsavedChanges = false;
        }
        finally
        {
            _isSuppressingDirtyTracking = false;
        }

        SetLocalizedStatus("Str_Behaviour_StatusProfileImported");
    }

    public void RefreshLocalizedProperties()
    {
        _isSuppressingDirtyTracking = true;
        try
        {
            StatusMessage = LocalizedFormatHelper.Format(_statusMessageKey, _statusMessageArgs);
            LaunchSettings.RefreshLocalizedProperties();
            Mods.RefreshLocalizedProperties();
            AccountManagement.RefreshLocalizedProperties();
            WindowIntegrations.RefreshLocalizedProperties();
        }
        finally
        {
            _isSuppressingDirtyTracking = false;
        }
    }

    private void OnLaunchSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSuppressingDirtyTracking || LaunchSettings.IsReloadingGlobalSettings) return;
        switch (e.PropertyName)
        {
            case nameof(LaunchSettingsViewModel.CdnTestResult):
            case nameof(LaunchSettingsViewModel.IsTestingCdn):
            case nameof(LaunchSettingsViewModel.IsNotTestingCdn):
            case nameof(LaunchSettingsViewModel.InstalledPlayerVersion):
            case nameof(LaunchSettingsViewModel.AppUpdateStatusText):
            case nameof(LaunchSettingsViewModel.IsCheckingAppUpdate):
            case nameof(LaunchSettingsViewModel.IsNotCheckingAppUpdate):
                return;
            case nameof(LaunchSettingsViewModel.HasUnsavedGlobalSettings):
                if (LaunchSettings.HasUnsavedGlobalSettings)
                {
                    HasUnsavedChanges = true;
                }
                return;
            default:
                HasUnsavedChanges = true;
                return;
        }
    }

    private void OnFastFlagsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSuppressingDirtyTracking || FastFlags.IsLoadingPresets) return;
        HasUnsavedChanges = true;
    }

    private void OnWindowIntegrationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSuppressingDirtyTracking) return;
        switch (e.PropertyName)
        {
            case nameof(WindowAndIntegrationsViewModel.CleanerStatusText):
            case nameof(WindowAndIntegrationsViewModel.PreviewTheme):
            case nameof(WindowAndIntegrationsViewModel.IsCustomBootstrapperIconSelected):
            case nameof(WindowAndIntegrationsViewModel.CustomIconWarningText):
            case nameof(WindowAndIntegrationsViewModel.HasCustomIconWarning):
            case nameof(WindowAndIntegrationsViewModel.CustomIconPreviewImage):
            case nameof(WindowAndIntegrationsViewModel.HasCustomIconPreview):
            case nameof(WindowAndIntegrationsViewModel.IsCustomRobloxIconSelected):
            case nameof(WindowAndIntegrationsViewModel.CustomRobloxIconWarningText):
            case nameof(WindowAndIntegrationsViewModel.HasCustomRobloxIconWarning):
            case nameof(WindowAndIntegrationsViewModel.CustomRobloxIconPreviewImage):
            case nameof(WindowAndIntegrationsViewModel.HasCustomRobloxIconPreview):
            case nameof(WindowAndIntegrationsViewModel.IsDiscordRpcSubOptionsEnabled):
                return;
            default:
                HasUnsavedChanges = true;
                return;
        }
    }

    private void OnAccountManagementPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSuppressingDirtyTracking) return;
        if (e.PropertyName is nameof(AccountsViewModel.MultiInstanceEnabled) or nameof(AccountsViewModel.MultiInstanceLaunchDelayMs))
        {
            HasUnsavedChanges = true;
        }
    }

    internal void DetachLocalization()
    {
        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
    }

    public void Dispose()
    {
        DetachLocalization();
        LaunchSettings.PropertyChanged -= OnLaunchSettingsPropertyChanged;
        FastFlags.PropertyChanged -= OnFastFlagsPropertyChanged;
        WindowIntegrations.PropertyChanged -= OnWindowIntegrationsPropertyChanged;
        AccountManagement.PropertyChanged -= OnAccountManagementPropertyChanged;
        WindowIntegrations.Dispose();
        AccountManagement.Dispose();
        GC.SuppressFinalize(this);
    }

    internal void SetLocalizedStatus(string key, params object?[] args)
    {
        _statusMessageKey = key;
        _statusMessageArgs = args ?? Array.Empty<object?>();
        StatusMessage = LocalizedFormatHelper.Format(_statusMessageKey, _statusMessageArgs);
    }

    public static Core.Models.WindowState ClampWindowBounds(
        Core.Models.WindowState state,
        double virtualLeft,
        double virtualTop,
        double virtualWidth,
        double virtualHeight)
    {
        ArgumentNullException.ThrowIfNull(state);

        double safeVirtualWidth = double.IsFinite(virtualWidth) && virtualWidth > 0 ? virtualWidth : 1040;
        double safeVirtualHeight = double.IsFinite(virtualHeight) && virtualHeight > 0 ? virtualHeight : 680;
        double safeVirtualLeft = double.IsFinite(virtualLeft) ? virtualLeft : 0;
        double safeVirtualTop = double.IsFinite(virtualTop) ? virtualTop : 0;

        double maxWidth = Math.Min(3840, safeVirtualWidth);
        double maxHeight = Math.Min(2160, safeVirtualHeight);
        double minWidth = Math.Min(640, maxWidth);
        double minHeight = Math.Min(480, maxHeight);

        double rawWidth = double.IsFinite(state.Width) && state.Width is >= 640 and <= 3840 ? state.Width : 1040;
        double rawHeight = double.IsFinite(state.Height) && state.Height is >= 480 and <= 2160 ? state.Height : 680;

        double width = Math.Clamp(rawWidth, minWidth, maxWidth);
        double height = Math.Clamp(rawHeight, minHeight, maxHeight);

        double maxLeft = Math.Max(safeVirtualLeft, safeVirtualLeft + safeVirtualWidth - width);
        double maxTop = Math.Max(safeVirtualTop, safeVirtualTop + safeVirtualHeight - height);

        double rawLeft = double.IsFinite(state.Left) ? state.Left : safeVirtualLeft;
        double rawTop = double.IsFinite(state.Top) ? state.Top : safeVirtualTop;

        double left = Math.Clamp(rawLeft, safeVirtualLeft, maxLeft);
        double top = Math.Clamp(rawTop, safeVirtualTop, maxTop);

        return new Core.Models.WindowState
        {
            Width = width,
            Height = height,
            Left = left,
            Top = top
        };
    }
}
