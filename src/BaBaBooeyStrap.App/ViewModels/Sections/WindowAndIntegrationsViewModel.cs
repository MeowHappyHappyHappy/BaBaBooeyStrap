using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BaBaBooeyStrap.App.Models;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Integrations.Cleaner;

namespace BaBaBooeyStrap.App.ViewModels.Sections;

public sealed class WindowAndIntegrationsViewModel : ViewModelBase
{
    private readonly Settings _settings;
    private readonly IPaths _paths;
    private readonly Action<string, object?[]> _setLocalizedStatus;

    private ThemeDefinition _previewTheme = XmlThemeParser.GetDefaultFluent();
    private string _cleanerStatusText = string.Empty;
    private string? _cleanerStatusKey;
    private object?[] _cleanerStatusArgs = Array.Empty<object?>();
    private string _customIconWarningText = string.Empty;
    private ImageSource? _customIconPreviewImage;

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
        set
        {
            if (_settings.BootstrapperIcon != value)
            {
                _settings.BootstrapperIcon = value;
                OnPropertyChanged();
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
            if (!string.Equals(_settings.BootstrapperIconCustomLocation, next, StringComparison.Ordinal))
            {
                _settings.BootstrapperIconCustomLocation = next;
                OnPropertyChanged();
                RefreshCustomIconState();
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

    // --- Integrations & Cleaner ---
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
    }

    private void RefreshCustomIconState()
    {
        if (!IsCustomBootstrapperIconSelected || string.IsNullOrWhiteSpace(BootstrapperIconCustomLocation))
        {
            CustomIconPreviewImage = null;
            CustomIconWarningText = string.Empty;
            return;
        }

        string path = BootstrapperIconCustomLocation.Trim();
        if (!File.Exists(path))
        {
            CustomIconPreviewImage = null;
            CustomIconWarningText = LocalizationService.Instance.GetString("Str_Appearance_CustomIconNotFound");
            return;
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

            CustomIconPreviewImage = bitmap;
            CustomIconWarningText = string.Empty;
        }
        catch
        {
            CustomIconPreviewImage = null;
            CustomIconWarningText = LocalizationService.Instance.GetString("Str_Appearance_CustomIconInvalid");
        }
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
