using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BaBaBooeyStrap.App.Models;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Integrations.Cleaner;

namespace BaBaBooeyStrap.App.ViewModels.Sections;

public sealed class WindowAndIntegrationsViewModel : ViewModelBase, IDisposable
{
    private readonly Settings _settings;
    private readonly IPaths _paths;
    private readonly Action<string, object?[]> _setLocalizedStatus;

    private ThemeDefinition _previewTheme = XmlThemeParser.GetDefaultFluent();
    private string _cleanerStatusText = string.Empty;
    private string? _cleanerStatusKey;
    private object?[] _cleanerStatusArgs = Array.Empty<object?>();
    private bool _isCleaning;
    private string _customIconWarningText = string.Empty;
    private ImageSource? _customIconPreviewImage;
    private string _customRobloxIconWarningText = string.Empty;
    private ImageSource? _customRobloxIconPreviewImage;
    private CancellationTokenSource? _iconPreviewDebounceCts;
    private CancellationTokenSource? _robloxIconPreviewDebounceCts;
    private bool _disposed;

    public WindowAndIntegrationsViewModel(
        Settings settings,
        IPaths paths,
        Action<string, object?[]> setLocalizedStatus)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _setLocalizedStatus = setLocalizedStatus ?? throw new ArgumentNullException(nameof(setLocalizedStatus));

        RefreshCustomThemes();
        RefreshCustomIconState();
        RefreshCustomRobloxIconState();
    }

    public event EventHandler<Theme>? ThemeChanged;
    public event EventHandler<AppLanguage>? LanguageChanged;

    public IReadOnlyList<Theme> AvailableThemes { get; } = Enum.GetValues<Theme>();
    public IReadOnlyList<AppLanguage> AvailableLanguages { get; } = Enum.GetValues<AppLanguage>();
    public IReadOnlyList<BootstrapperStyle> AvailableBootstrapperStyles { get; } = Enum.GetValues<BootstrapperStyle>();
    public IReadOnlyList<BootstrapperIcon> AvailableBootstrapperIcons { get; } = Enum.GetValues<BootstrapperIcon>();
    public IReadOnlyList<RobloxIcon> AvailableRobloxIcons { get; } = Enum.GetValues<RobloxIcon>();
    public IReadOnlyList<DiscordRPCStatusDisplay> AvailableRpcDisplays { get; } = Enum.GetValues<DiscordRPCStatusDisplay>();
    public IReadOnlyList<CleanerOptions> AvailableCleanerOptions { get; } = Enum.GetValues<CleanerOptions>();
    public ObservableCollection<string> AvailableCustomThemes { get; } = new();

    // --- Window & Appearance ---
    public Theme CurrentTheme
    {
        get => _settings.Theme;
        set
        {
            if (SetModelProperty(_settings.Theme, value, v => _settings.Theme = v))
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
            if (SetModelProperty(_settings.Language, value, v => _settings.Language = v))
            {
                LocalizationService.Instance.ApplyLanguage(value);
                LanguageChanged?.Invoke(this, value);
            }
        }
    }

    public byte AcrylicBackgroundOpacity
    {
        get => _settings.AcrylicBackgroundOpacity;
        set => SetModelProperty(_settings.AcrylicBackgroundOpacity, value, v => _settings.AcrylicBackgroundOpacity = v);
    }

    public bool UseAcrylicBackground
    {
        get => _settings.UseAcrylicBackground;
        set => SetModelProperty(_settings.UseAcrylicBackground, value, v => _settings.UseAcrylicBackground = v);
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set => SetModelProperty(_settings.MinimizeToTray, value, v => _settings.MinimizeToTray = v);
    }

    public BootstrapperStyle SelectedBootstrapperStyle
    {
        get => _settings.BootstrapperStyle;
        set => SetModelProperty(_settings.BootstrapperStyle, value, v => _settings.BootstrapperStyle = v);
    }

    public string BootstrapperTitle
    {
        get => _settings.BootstrapperTitle;
        set => SetModelProperty(_settings.BootstrapperTitle, value, v => _settings.BootstrapperTitle = v);
    }

    public BootstrapperIcon SelectedBootstrapperIcon
    {
        get => _settings.BootstrapperIcon;
        set
        {
            if (SetModelProperty(_settings.BootstrapperIcon, value, v => _settings.BootstrapperIcon = v))
            {
                OnPropertyChanged(nameof(IsCustomBootstrapperIconSelected));
                RefreshCustomIconState();
            }
        }
    }

    public bool IsCustomBootstrapperIconSelected => SelectedBootstrapperIcon == BootstrapperIcon.IconCustom;

    public string BootstrapperIconCustomLocation
    {
        get => _settings.BootstrapperIconCustomLocation;
        set
        {
            string next = value ?? string.Empty;
            if (SetModelProperty(_settings.BootstrapperIconCustomLocation, next, v => _settings.BootstrapperIconCustomLocation = v))
            {
                ScheduleIconPreviewRefresh(ref _iconPreviewDebounceCts, RefreshCustomIconState);
            }
        }
    }

    public string CustomIconWarningText
    {
        get => _customIconWarningText;
        private set
        {
            if (SetProperty(ref _customIconWarningText, value))
            {
                OnPropertyChanged(nameof(HasCustomIconWarning));
            }
        }
    }

    public bool HasCustomIconWarning => !string.IsNullOrEmpty(_customIconWarningText);

    public ImageSource? CustomIconPreviewImage
    {
        get => _customIconPreviewImage;
        private set
        {
            if (SetProperty(ref _customIconPreviewImage, value))
            {
                OnPropertyChanged(nameof(HasCustomIconPreview));
            }
        }
    }

    public bool HasCustomIconPreview => _customIconPreviewImage != null;

    public string? SelectedCustomTheme
    {
        get => _settings.SelectedCustomTheme;
        set
        {
            if (SetModelProperty(_settings.SelectedCustomTheme, value, v => _settings.SelectedCustomTheme = v))
            {
                UpdatePreviewTheme();
            }
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
        set => SetModelProperty(_settings.RobloxTitle, value, v => _settings.RobloxTitle = v);
    }

    public RobloxIcon SelectedRobloxIcon
    {
        get => _settings.RobloxIcon;
        set
        {
            if (SetModelProperty(_settings.RobloxIcon, value, v => _settings.RobloxIcon = v))
            {
                OnPropertyChanged(nameof(IsCustomRobloxIconSelected));
                RefreshCustomRobloxIconState();
            }
        }
    }

    public bool IsCustomRobloxIconSelected => SelectedRobloxIcon == RobloxIcon.IconCustom;

    public string RobloxIconCustomLocation
    {
        get => _settings.RobloxIconCustomLocation;
        set
        {
            string next = value ?? string.Empty;
            if (SetModelProperty(_settings.RobloxIconCustomLocation, next, v => _settings.RobloxIconCustomLocation = v))
            {
                ScheduleIconPreviewRefresh(ref _robloxIconPreviewDebounceCts, RefreshCustomRobloxIconState);
            }
        }
    }

    public string CustomRobloxIconWarningText
    {
        get => _customRobloxIconWarningText;
        private set
        {
            if (SetProperty(ref _customRobloxIconWarningText, value))
            {
                OnPropertyChanged(nameof(HasCustomRobloxIconWarning));
            }
        }
    }

    public bool HasCustomRobloxIconWarning => !string.IsNullOrEmpty(_customRobloxIconWarningText);

    public ImageSource? CustomRobloxIconPreviewImage
    {
        get => _customRobloxIconPreviewImage;
        private set
        {
            if (SetProperty(ref _customRobloxIconPreviewImage, value))
            {
                OnPropertyChanged(nameof(HasCustomRobloxIconPreview));
            }
        }
    }

    public bool HasCustomRobloxIconPreview => _customRobloxIconPreviewImage != null;

    // --- Integrations & Cleaner ---
    public bool EnableActivityTracking
    {
        get => _settings.EnableActivityTracking;
        set
        {
            if (_settings.EnableActivityTracking != value)
            {
                _settings.EnableActivityTracking = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDiscordRpcSubOptionsEnabled));
            }
        }
    }

    public bool UseDiscordRichPresence
    {
        get => _settings.UseDiscordRichPresence;
        set
        {
            if (_settings.UseDiscordRichPresence != value)
            {
                _settings.UseDiscordRichPresence = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDiscordRpcSubOptionsEnabled));
            }
        }
    }

    public bool IsDiscordRpcSubOptionsEnabled => EnableActivityTracking && UseDiscordRichPresence;

    public DiscordRPCStatusDisplay RichPresenceStatusDisplayType { get => _settings.RichPresenceStatusDisplayType; set => SetModelProperty(_settings.RichPresenceStatusDisplayType, value, v => _settings.RichPresenceStatusDisplayType = v); }
    public bool HideRPCButtons { get => _settings.HideRPCButtons; set => SetModelProperty(_settings.HideRPCButtons, value, v => _settings.HideRPCButtons = v); }
    public bool ShowAccountOnRichPresence { get => _settings.ShowAccountOnRichPresence; set => SetModelProperty(_settings.ShowAccountOnRichPresence, value, v => _settings.ShowAccountOnRichPresence = v); }
    public bool ShowServerDetails { get => _settings.ShowServerDetails; set => SetModelProperty(_settings.ShowServerDetails, value, v => _settings.ShowServerDetails = v); }
    public bool EnableWindowManipulation { get => _settings.EnableWindowManipulation; set => SetModelProperty(_settings.EnableWindowManipulation, value, v => _settings.EnableWindowManipulation = v); }
    public bool FakeBorderlessFullscreen { get => _settings.FakeBorderlessFullscreen; set => SetModelProperty(_settings.FakeBorderlessFullscreen, value, v => _settings.FakeBorderlessFullscreen = v); }
    public bool EnableBetterMatchmaking { get => _settings.EnableBetterMatchmaking; set => SetModelProperty(_settings.EnableBetterMatchmaking, value, v => _settings.EnableBetterMatchmaking = v); }
    public bool EnableBetterMatchmakingRandomization { get => _settings.EnableBetterMatchmakingRandomization; set => SetModelProperty(_settings.EnableBetterMatchmakingRandomization, value, v => _settings.EnableBetterMatchmakingRandomization = v); }

    public CleanerOptions SelectedCleanerOption
    {
        get => _settings.CleanerOptions;
        set => SetModelProperty(_settings.CleanerOptions, value, v => _settings.CleanerOptions = v);
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

    public bool IsCleaning
    {
        get => _isCleaning;
        private set
        {
            if (SetProperty(ref _isCleaning, value))
            {
                OnPropertyChanged(nameof(IsNotCleaning));
            }
        }
    }

    public bool IsNotCleaning => !IsCleaning;

    public ObservableCollection<CustomIntegration> CustomIntegrations => _settings.CustomIntegrations;

    public void AddCustomIntegration(CustomIntegration integration)
    {
        ArgumentNullException.ThrowIfNull(integration);
        _settings.CustomIntegrations.Add(integration);
        OnPropertyChanged(nameof(CustomIntegrations));
    }

    public void UpdateCustomIntegration(int index, CustomIntegration updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        if (index < 0 || index >= _settings.CustomIntegrations.Count) return;
        _settings.CustomIntegrations[index] = updated;
        OnPropertyChanged(nameof(CustomIntegrations));
    }

    public bool RemoveCustomIntegration(CustomIntegration integration)
    {
        if (integration == null) return false;
        bool removed = _settings.CustomIntegrations.Remove(integration);
        if (removed)
        {
            OnPropertyChanged(nameof(CustomIntegrations));
        }
        return removed;
    }

    public void RefreshFromSettings()
    {
        OnPropertyChanged(nameof(CurrentTheme));
        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged(nameof(AcrylicBackgroundOpacity));
        OnPropertyChanged(nameof(UseAcrylicBackground));
        OnPropertyChanged(nameof(MinimizeToTray));
        OnPropertyChanged(nameof(SelectedBootstrapperStyle));
        OnPropertyChanged(nameof(BootstrapperTitle));
        OnPropertyChanged(nameof(SelectedBootstrapperIcon));
        OnPropertyChanged(nameof(IsCustomBootstrapperIconSelected));
        OnPropertyChanged(nameof(BootstrapperIconCustomLocation));
        OnPropertyChanged(nameof(SelectedRobloxIcon));
        OnPropertyChanged(nameof(IsCustomRobloxIconSelected));
        OnPropertyChanged(nameof(RobloxTitle));
        OnPropertyChanged(nameof(RobloxIconCustomLocation));
        OnPropertyChanged(nameof(SelectedCustomTheme));
        OnPropertyChanged(nameof(EnableWindowManipulation));
        OnPropertyChanged(nameof(FakeBorderlessFullscreen));
        OnPropertyChanged(nameof(EnableActivityTracking));
        OnPropertyChanged(nameof(UseDiscordRichPresence));
        OnPropertyChanged(nameof(IsDiscordRpcSubOptionsEnabled));
        OnPropertyChanged(nameof(RichPresenceStatusDisplayType));
        OnPropertyChanged(nameof(HideRPCButtons));
        OnPropertyChanged(nameof(ShowAccountOnRichPresence));
        OnPropertyChanged(nameof(ShowServerDetails));
        OnPropertyChanged(nameof(EnableBetterMatchmaking));
        OnPropertyChanged(nameof(EnableBetterMatchmakingRandomization));
        OnPropertyChanged(nameof(SelectedCleanerOption));
        OnPropertyChanged(nameof(CleanRobloxCache));
        OnPropertyChanged(nameof(CleanRobloxStudioCache));
        OnPropertyChanged(nameof(CleanRobloxLogs));
        OnPropertyChanged(nameof(CleanBaBaBooeyStrapLogs));
        OnPropertyChanged(nameof(CleanRobloxCrashDumps));
        OnPropertyChanged(nameof(CustomIntegrations));

        LocalizationService.Instance.ApplyLanguage(_settings.Language);
        ThemeChanged?.Invoke(this, _settings.Theme);
        RefreshCustomThemes();
        RefreshCustomIconState();
        RefreshCustomRobloxIconState();
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
        try
        {
            var report = ExecuteCleanerCore();
            ApplyCleanerReport(report);
        }
        catch (Exception ex)
        {
            ApplyCleanerError(ex);
        }
    }

    public async Task RunCleanerNowAsync()
    {
        if (IsCleaning) return;

        IsCleaning = true;
        try
        {
            var policy = CleanerPolicyMapper.FromSettings(_settings);
            var cleaner = new CacheCleaner(_paths);
            var report = await Task.Run(() => cleaner.ExecuteCleanup(policy)).ConfigureAwait(true);
            ApplyCleanerReport(report);
        }
        catch (Exception ex)
        {
            ApplyCleanerError(ex);
        }
        finally
        {
            IsCleaning = false;
        }
    }

    private CleanerReport ExecuteCleanerCore()
    {
        var cleaner = new CacheCleaner(_paths);
        var policy = CleanerPolicyMapper.FromSettings(_settings);
        return cleaner.ExecuteCleanup(policy);
    }

    private void ApplyCleanerReport(CleanerReport report)
    {
        double freedMb = report.BytesFreed / (1024.0 * 1024.0);
        _cleanerStatusKey = "Str_Behaviour_CleanerReportFormat";
        _cleanerStatusArgs = [report.FilesDeleted, freedMb];
        CleanerStatusText = LocalizedFormatHelper.Format(_cleanerStatusKey, _cleanerStatusArgs);
        _setLocalizedStatus(_cleanerStatusKey, _cleanerStatusArgs);
    }

    private void ApplyCleanerError(Exception ex)
    {
        Logger.ExceptionStatic("WindowAndIntegrationsViewModel.RunCleanerNow", ex);
        _cleanerStatusKey = "Str_Behaviour_CleanerErrorFormat";
        _cleanerStatusArgs = [ex.Message];
        CleanerStatusText = LocalizedFormatHelper.Format(_cleanerStatusKey, _cleanerStatusArgs);
        _setLocalizedStatus(_cleanerStatusKey, _cleanerStatusArgs);
    }

    public void RefreshLocalizedProperties()
    {
        if (!string.IsNullOrEmpty(_cleanerStatusKey))
        {
            CleanerStatusText = LocalizedFormatHelper.Format(_cleanerStatusKey, _cleanerStatusArgs);
        }

        RefreshCustomIconState();
        RefreshCustomRobloxIconState();
    }

    private void RefreshCustomRobloxIconState()
    {
        var (img, warn) = ResolveIconPreview(IsCustomRobloxIconSelected, RobloxIconCustomLocation, allowPeResources: true);
        CustomRobloxIconPreviewImage = img;
        CustomRobloxIconWarningText = warn;
    }

    private void RefreshCustomIconState()
    {
        var (img, warn) = ResolveIconPreview(IsCustomBootstrapperIconSelected, BootstrapperIconCustomLocation, allowPeResources: false);
        CustomIconPreviewImage = img;
        CustomIconWarningText = warn;
    }

    private static (ImageSource? Image, string Warning) ResolveIconPreview(
        bool isSelected,
        string? rawLocation,
        bool allowPeResources)
    {
        if (!isSelected || string.IsNullOrWhiteSpace(rawLocation))
        {
            return (null, string.Empty);
        }

        string path = rawLocation.Trim();
        if (!File.Exists(path))
        {
            return (null, LocalizationService.Instance.GetString("Str_Appearance_CustomIconNotFound"));
        }

        if (allowPeResources)
        {
            string ext = Path.GetExtension(path);
            if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return (IconResolver.ResolveImageSource(BootstrapperIcon.IconCustom, path), string.Empty);
            }
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 32;
            bitmap.EndInit();
            bitmap.Freeze();

            return (bitmap, string.Empty);
        }
        catch
        {
            return (null, LocalizationService.Instance.GetString("Str_Appearance_CustomIconInvalid"));
        }
    }

    private void ScheduleIconPreviewRefresh(ref CancellationTokenSource? ctsSlot, Action refreshAction)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (_disposed || dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            refreshAction();
            return;
        }

        var oldCts = Interlocked.Exchange(ref ctsSlot, new CancellationTokenSource());
        try { oldCts?.Cancel(); } catch { }
        try { oldCts?.Dispose(); } catch { }

        var token = ctsSlot!.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                {
                    await dispatcher.InvokeAsync(refreshAction, DispatcherPriority.Background);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var c1 = Interlocked.Exchange(ref _iconPreviewDebounceCts, null);
        try { c1?.Cancel(); } catch { }
        try { c1?.Dispose(); } catch { }

        var c2 = Interlocked.Exchange(ref _robloxIconPreviewDebounceCts, null);
        try { c2?.Cancel(); } catch { }
        try { c2?.Dispose(); } catch { }
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
