using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Window;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class WindowSwitcherSettingsDialog : FluentWindow
{
    private sealed record MonitorOption(string? DeviceName, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record TilingModeOption(WindowTilingMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly Settings _settings;
    private readonly WindowSwitcherCoordinator? _coordinator;
    private readonly IJsonManager<Settings>? _settingsManager;

    public WindowSwitcherSettingsDialog(
        Settings settings,
        WindowSwitcherCoordinator? coordinator,
        IJsonManager<Settings>? settingsManager = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _coordinator = coordinator;
        _settingsManager = settingsManager;

        InitializeComponent();
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        AutoTagToggle.IsChecked = _settings.AutoTagMultiInstanceWindowTitle;
        TitleTemplateBox.Text = _settings.MultiInstanceWindowTitleTemplate;
        UpdateTitlePreview();

        QuickHotkeyEnabledCheck.IsChecked = _settings.EnableQuickSwitcherHotkey;
        QuickHotkeyBox.Text = _settings.QuickSwitcherHotkey;

        CycleHotkeyEnabledCheck.IsChecked = _settings.EnableCycleWindowHotkey;
        CycleHotkeyBox.Text = _settings.CycleWindowHotkey;

        CyclePrevHotkeyEnabledCheck.IsChecked = _settings.EnableCyclePreviousWindowHotkey;
        CyclePrevHotkeyBox.Text = _settings.CyclePreviousWindowHotkey;

        DirectSlotsEnabledCheck.IsChecked = _settings.EnableDirectSlotHotkeys;
        DirectSlotModifierBox.Text = _settings.DirectSlotHotkeyModifier;

        UpdateHotkeyStatusTexts();

        SwitchHighlightToggle.IsChecked = _settings.EnableWindowSwitchHighlight;
        FloatingBarEnabledToggle.IsChecked = _settings.FloatingSwitcherBarEnabled;
        FloatingBarAutoShowToggle.IsChecked = _settings.FloatingSwitcherBarAutoShow;

        var modeOptions = new List<TilingModeOption>
        {
            new(WindowTilingMode.SingleMonitorGrid, LocalizationService.Instance.GetString("Str_Tiling_SingleMonitorGrid")),
            new(WindowTilingMode.SpreadAcrossMonitors, LocalizationService.Instance.GetString("Str_Tiling_SpreadAcrossMonitors")),
            new(WindowTilingMode.MainAndSide, LocalizationService.Instance.GetString("Str_Tiling_MainAndSide"))
        };
        TilingModeCombo.ItemsSource = modeOptions;
        TilingModeCombo.SelectedItem = modeOptions.FirstOrDefault(m => m.Mode == _settings.DefaultWindowTilingMode) ?? modeOptions[0];

        var monitors = _coordinator?.GetMonitors() ?? new Win32MonitorProvider().GetMonitors();
        var autoOption = new MonitorOption(null, LocalizationService.Instance.GetString("Str_Tiling_MonitorCursorAuto"));
        var monitorChoices = new List<MonitorOption> { autoOption };
        monitorChoices.AddRange(monitors.Select(m => new MonitorOption(m.DeviceName, m.DisplayLabel)));

        PreferredMonitorCombo.ItemsSource = monitorChoices;
        PreferredMonitorCombo.SelectedItem = monitorChoices.FirstOrDefault(m =>
            string.Equals(m.DeviceName, _settings.PreferredTilingMonitorDevice, StringComparison.OrdinalIgnoreCase)) ?? autoOption;

        var explicitMonitors = monitors.Select(m => new MonitorOption(m.DeviceName, m.DisplayLabel)).ToList();
        var autoDefaultMain = new MonitorOption(null, LocalizationService.Instance.GetString("Str_SwitcherSettings_AutoPrimaryMonitor"));
        var autoDefaultSide = new MonitorOption(null, LocalizationService.Instance.GetString("Str_SwitcherSettings_AutoSecondaryMonitor"));

        var mainChoices = new List<MonitorOption> { autoDefaultMain };
        mainChoices.AddRange(explicitMonitors);
        MainMonitorCombo.ItemsSource = mainChoices;
        MainMonitorCombo.SelectedItem = mainChoices.FirstOrDefault(m =>
            string.Equals(m.DeviceName, _settings.MainSidePrimaryMonitorDevice, StringComparison.OrdinalIgnoreCase)) ?? autoDefaultMain;

        var sideChoices = new List<MonitorOption> { autoDefaultSide };
        sideChoices.AddRange(explicitMonitors);
        SideMonitorCombo.ItemsSource = sideChoices;
        SideMonitorCombo.SelectedItem = sideChoices.FirstOrDefault(m =>
            string.Equals(m.DeviceName, _settings.MainSideSecondaryMonitorDevice, StringComparison.OrdinalIgnoreCase)) ?? autoDefaultSide;
    }

    private void UpdateTitlePreview()
    {
        string template = string.IsNullOrWhiteSpace(TitleTemplateBox.Text)
            ? Settings.DefaultMultiInstanceWindowTitleTemplate
            : TitleTemplateBox.Text;

        var sample = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Index"] = "1",
            ["Server"] = "JP",
            ["ServerCode"] = "JP",
            ["Account"] = "PlayerOne",
            ["AccountName"] = "PlayerOne",
            ["DisplayName"] = "Player",
            ["Notes"] = "Main",
            ["Pid"] = "4812",
            ["OriginalTitle"] = "Roblox"
        };

        string formatted = WindowTitleFormatter.Format(template, sample);
        TitlePreviewText.Text = LocalizationService.Instance.Format("Str_SwitcherSettings_PreviewFormat", formatted);
    }

    private void UpdateHotkeyStatusTexts()
    {
        var hotkeySvc = _coordinator?.HotkeyService;
        QuickHotkeyStatusText.Text = FormatHotkeyStatus(
            hotkeySvc,
            GlobalHotkeyService.KeyQuickSwitcher,
            QuickHotkeyEnabledCheck.IsChecked == true);
        CycleHotkeyStatusText.Text = FormatHotkeyStatus(
            hotkeySvc,
            GlobalHotkeyService.KeyCycleWindow,
            CycleHotkeyEnabledCheck.IsChecked == true);
        CyclePrevHotkeyStatusText.Text = FormatHotkeyStatus(
            hotkeySvc,
            GlobalHotkeyService.KeyCyclePreviousWindow,
            CyclePrevHotkeyEnabledCheck.IsChecked == true);
        DirectSlotsStatusText.Text = FormatHotkeyStatus(
            hotkeySvc,
            GlobalHotkeyService.KeyDirectSlots,
            DirectSlotsEnabledCheck.IsChecked == true,
            appendRange: true);
    }

    private static string FormatHotkeyStatus(GlobalHotkeyService? svc, string key, bool enabled, bool appendRange = false)
    {
        string rangePrefix = appendRange ? "+ 1..9 · " : string.Empty;
        if (!enabled)
        {
            return rangePrefix + LocalizationService.Instance.GetString("Str_Common_Disabled");
        }

        if (svc == null)
        {
            return rangePrefix + LocalizationService.Instance.GetString("Str_Common_Ready");
        }

        return rangePrefix + LocalizationService.Instance.GetString(
            svc.IsRegistered(key) ? "Str_SwitcherSettings_HotkeyActive" : "Str_SwitcherSettings_HotkeyConflict");
    }

    private void TitleTemplateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTitlePreview();
    }

    private void QuickHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        string? formatted = HotkeyParser.FormatKeyGesture(Keyboard.Modifiers, actualKey);
        if (!string.IsNullOrEmpty(formatted))
        {
            QuickHotkeyBox.Text = formatted;
        }
    }

    private void CycleHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        string? formatted = HotkeyParser.FormatKeyGesture(Keyboard.Modifiers, actualKey);
        if (!string.IsNullOrEmpty(formatted))
        {
            CycleHotkeyBox.Text = formatted;
        }
    }

    private void CyclePrevHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        string? formatted = HotkeyParser.FormatKeyGesture(Keyboard.Modifiers, actualKey);
        if (!string.IsNullOrEmpty(formatted))
        {
            CyclePrevHotkeyBox.Text = formatted;
        }
    }

    private void DirectSlotModifierBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        string mods = HotkeyParser.FormatModifiersOnly(Keyboard.Modifiers);
        if (!string.IsNullOrEmpty(mods))
        {
            DirectSlotModifierBox.Text = mods;
        }
    }

    private void ResetQuickHotkey_Click(object sender, RoutedEventArgs e) => QuickHotkeyBox.Text = "Ctrl+Alt+Space";
    private void ResetCycleHotkey_Click(object sender, RoutedEventArgs e) => CycleHotkeyBox.Text = "Ctrl+Alt+Right";
    private void ResetCyclePrevHotkey_Click(object sender, RoutedEventArgs e) => CyclePrevHotkeyBox.Text = "Ctrl+Alt+Left";
    private void ResetDirectSlots_Click(object sender, RoutedEventArgs e) => DirectSlotModifierBox.Text = "Ctrl+Alt";

    private void ResetFloatingBarPos_Click(object sender, RoutedEventArgs e)
    {
        _coordinator?.ResetFloatingBarPosition();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoTagMultiInstanceWindowTitle = AutoTagToggle.IsChecked == true;
        _settings.MultiInstanceWindowTitleTemplate = string.IsNullOrWhiteSpace(TitleTemplateBox.Text)
            ? Settings.DefaultMultiInstanceWindowTitleTemplate
            : TitleTemplateBox.Text.Trim();

        _settings.EnableQuickSwitcherHotkey = QuickHotkeyEnabledCheck.IsChecked == true;
        if (HotkeyParser.TryParse(QuickHotkeyBox.Text, out var parsedQuick))
        {
            _settings.QuickSwitcherHotkey = parsedQuick.NormalizedText;
        }

        _settings.EnableCycleWindowHotkey = CycleHotkeyEnabledCheck.IsChecked == true;
        if (HotkeyParser.TryParse(CycleHotkeyBox.Text, out var parsedCycle))
        {
            _settings.CycleWindowHotkey = parsedCycle.NormalizedText;
        }

        _settings.EnableCyclePreviousWindowHotkey = CyclePrevHotkeyEnabledCheck.IsChecked == true;
        if (HotkeyParser.TryParse(CyclePrevHotkeyBox.Text, out var parsedCyclePrev))
        {
            _settings.CyclePreviousWindowHotkey = parsedCyclePrev.NormalizedText;
        }

        _settings.EnableDirectSlotHotkeys = DirectSlotsEnabledCheck.IsChecked == true;
        if (HotkeyParser.TryParseModifiersOnly(DirectSlotModifierBox.Text, out _, out string normMods))
        {
            _settings.DirectSlotHotkeyModifier = normMods;
        }

        _settings.EnableWindowSwitchHighlight = SwitchHighlightToggle.IsChecked == true;
        _settings.FloatingSwitcherBarEnabled = FloatingBarEnabledToggle.IsChecked == true;
        _settings.FloatingSwitcherBarAutoShow = FloatingBarAutoShowToggle.IsChecked == true;

        if (TilingModeCombo.SelectedItem is TilingModeOption modeOpt)
        {
            _settings.DefaultWindowTilingMode = modeOpt.Mode;
        }

        _settings.PreferredTilingMonitorDevice = (PreferredMonitorCombo.SelectedItem as MonitorOption)?.DeviceName;
        _settings.MainSidePrimaryMonitorDevice = (MainMonitorCombo.SelectedItem as MonitorOption)?.DeviceName;
        _settings.MainSideSecondaryMonitorDevice = (SideMonitorCombo.SelectedItem as MonitorOption)?.DeviceName;

        _settingsManager?.Save();
        _coordinator?.ReloadSettings();
        DialogResult = true;
        Close();
    }
}
