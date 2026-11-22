using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class FirstRunDialog : FluentWindow
{
    private readonly Settings _settings;
    private readonly bool _isInitializing;

    public bool CreateDesktopShortcut { get; private set; } = true;
    public bool RegisterProtocols { get; private set; } = true;
    public bool EnableMultiInstanceAndTray { get; private set; } = true;
    public bool RunOnStartup { get; private set; }

    public FirstRunDialog(Settings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _isInitializing = true;

        InitializeComponent();

        LanguageCombo.ItemsSource = Enum.GetValues<AppLanguage>();
        LanguageCombo.SelectedItem = _settings.Language;

        ThemeCombo.ItemsSource = Enum.GetValues<Theme>();
        ThemeCombo.SelectedItem = _settings.Theme;

        MultiInstanceTrayCheck.IsChecked = _settings.MultiInstanceEnabled && _settings.MinimizeToTray;
        RunOnStartupCheck.IsChecked = _settings.RunOnStartup;

        _isInitializing = false;
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || LanguageCombo.SelectedItem is not AppLanguage lang) return;
        _settings.Language = lang;
        LocalizationService.Instance.ApplyLanguage(lang);
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || ThemeCombo.SelectedItem is not Theme theme) return;
        _settings.Theme = theme;
        ThemeService.ApplyTheme(
            theme,
            _settings.UseAcrylicBackground ? WindowBackdropType.Acrylic : WindowBackdropType.Mica,
            this);
    }

    private void GetStarted_Click(object sender, RoutedEventArgs e)
    {
        CreateDesktopShortcut = DesktopShortcutCheck.IsChecked == true;
        RegisterProtocols = RegisterProtocolsCheck.IsChecked == true;
        EnableMultiInstanceAndTray = MultiInstanceTrayCheck.IsChecked == true;
        RunOnStartup = RunOnStartupCheck.IsChecked == true;

        if (LanguageCombo.SelectedItem is AppLanguage lang)
        {
            _settings.Language = lang;
        }
        if (ThemeCombo.SelectedItem is Theme theme)
        {
            _settings.Theme = theme;
        }

        _settings.MultiInstanceEnabled = EnableMultiInstanceAndTray;
        _settings.MinimizeToTray = EnableMultiInstanceAndTray;
        _settings.RunOnStartup = RunOnStartup;

        DialogResult = true;
        Close();
    }
}
