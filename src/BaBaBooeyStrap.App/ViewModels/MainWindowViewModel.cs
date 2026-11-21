using System.Collections.ObjectModel;
using System.ComponentModel;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels.Sections;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Launch;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.MultiInstance.Abstractions;

namespace BaBaBooeyStrap.App.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly Settings _settings;
    private readonly IJsonManager<State>? _stateManager;
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
        IJoinTargetStore? joinTargetStore = null)
    {
        _settings = settings ?? new Settings();
        var resolvedPaths = paths ?? Paths.Current;
        var resolvedSupervisor = processSupervisor ?? new ProcessSupervisor();
        var resolvedGlobalSettings = globalSettingsManager ?? new GlobalSettingsManager(resolvedPaths.RobloxGlobalSettings);
        var resolvedJoinTargetStore = joinTargetStore ?? new JoinTargetStore(resolvedPaths.TargetsFile);
        var fastFlagManager = new FastFlagManager(resolvedPaths.GetClientSettingsPath());
        _stateManager = stateManager;
        var installerService = new InstallerService(resolvedPaths, stateManager);

        LaunchSettings = new LaunchSettingsViewModel(
            _settings,
            resolvedPaths,
            resolvedGlobalSettings,
            stateManager,
            robloxStateManager,
            installerService,
            SetLocalizedStatus);

        FastFlags = new FastFlagsViewModel(_settings, fastFlagManager);

        Mods = new ModsViewModel(resolvedPaths, SetLocalizedStatus);

        AccountManagement = new AccountsViewModel(
            _settings,
            resolvedPaths,
            accountStore,
            resolvedJoinTargetStore,
            multiInstanceController,
            resolvedSupervisor,
            launcher,
            resolvedGlobalSettings,
            stateManager,
            SetLocalizedStatus);

        WindowIntegrations = new WindowAndIntegrationsViewModel(_settings, resolvedPaths, SetLocalizedStatus);

        LaunchSettings.PropertyChanged += OnLaunchSettingsPropertyChanged;
        FastFlags.PropertyChanged += OnFastFlagsPropertyChanged;
        WindowIntegrations.PropertyChanged += OnWindowIntegrationsPropertyChanged;
        AccountManagement.PropertyChanged += OnAccountManagementPropertyChanged;

        _languageChangedHandler = (_, _) => RefreshLocalizedProperties();
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
    }

    public Settings Settings => _settings;

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
        if (LaunchSettings.HasUnsavedGlobalSettings)
        {
            try
            {
                LaunchSettings.SaveGlobalSettings();
            }
            catch
            {
                // Keep general settings persistence resilient even if Roblox settings path is locked
            }
        }

        manager.Save();
        if (_stateManager != null)
        {
            AccountManagement.PersistLastTargetState();
            _stateManager.Save();
        }

        HasUnsavedChanges = false;
        SetLocalizedStatus("Str_MainWindow_StatusSavedAt", DateTime.Now.ToString("HH:mm:ss"));
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
}
