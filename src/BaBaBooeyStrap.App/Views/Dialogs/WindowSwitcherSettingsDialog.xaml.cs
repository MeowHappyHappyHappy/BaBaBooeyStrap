using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Integrations.Window;
using Wpf.Ui.Controls;
using TextBox = Wpf.Ui.Controls.TextBox;

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
        RestoreAccountBoundsToggle.IsChecked = _settings.RestoreAccountWindowBounds;
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

    private void QuickHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e) => CaptureHotkeyGesture(e, QuickHotkeyBox);

    private void CycleHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e) => CaptureHotkeyGesture(e, CycleHotkeyBox);

    private void CyclePrevHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e) => CaptureHotkeyGesture(e, CyclePrevHotkeyBox);

    private static void CaptureHotkeyGesture(KeyEventArgs e, TextBox targetBox)
    {
        Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ShouldAllowDialogNavigationKey(actualKey))
        {
            return;
        }

        e.Handled = true;
        string? formatted = HotkeyParser.FormatKeyGesture(Keyboard.Modifiers, actualKey);
        if (!string.IsNullOrEmpty(formatted))
        {
            targetBox.Text = formatted;
        }
    }

    private void DirectSlotModifierBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ShouldAllowDialogNavigationKey(actualKey))
        {
            return;
        }

        e.Handled = true;
        string mods = HotkeyParser.FormatModifiersOnly(Keyboard.Modifiers);
        if (!string.IsNullOrEmpty(mods))
        {
            DirectSlotModifierBox.Text = mods;
        }
    }

    private static bool ShouldAllowDialogNavigationKey(Key actualKey)
    {
        var nonShiftModifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows);
        if (actualKey == Key.Tab && nonShiftModifiers == ModifierKeys.None)
        {
            return true;
        }

        return actualKey == Key.Escape && Keyboard.Modifiers == ModifierKeys.None;
    }

    private void ResetQuickHotkey_Click(object sender, RoutedEventArgs e) => QuickHotkeyBox.Text = "Ctrl+Alt+Space";
    private void ResetCycleHotkey_Click(object sender, RoutedEventArgs e) => CycleHotkeyBox.Text = "Ctrl+Alt+Right";
    private void ResetCyclePrevHotkey_Click(object sender, RoutedEventArgs e) => CyclePrevHotkeyBox.Text = "Ctrl+Alt+Left";
    private void ResetDirectSlots_Click(object sender, RoutedEventArgs e) => DirectSlotModifierBox.Text = "Ctrl+Alt";

    private void ResetFloatingBarPos_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _coordinator?.ResetFloatingBarPosition();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("WindowSwitcherSettingsDialog.ResetFloatingBarPos", ex);
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                this);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool quickEnabled = QuickHotkeyEnabledCheck.IsChecked == true;
            bool cycleEnabled = CycleHotkeyEnabledCheck.IsChecked == true;
            bool cyclePrevEnabled = CyclePrevHotkeyEnabledCheck.IsChecked == true;
            bool directSlotsEnabled = DirectSlotsEnabledCheck.IsChecked == true;

            string quickNormalized = HotkeyParser.TryParse(QuickHotkeyBox.Text, out var parsedQuick)
                ? parsedQuick.NormalizedText
                : _settings.QuickSwitcherHotkey;
            string cycleNormalized = HotkeyParser.TryParse(CycleHotkeyBox.Text, out var parsedCycle)
                ? parsedCycle.NormalizedText
                : _settings.CycleWindowHotkey;
            string cyclePrevNormalized = HotkeyParser.TryParse(CyclePrevHotkeyBox.Text, out var parsedCyclePrev)
                ? parsedCyclePrev.NormalizedText
                : _settings.CyclePreviousWindowHotkey;
            string directModsNormalized = HotkeyParser.TryParseModifiersOnly(DirectSlotModifierBox.Text, out _, out string normMods)
                ? normMods
                : _settings.DirectSlotHotkeyModifier;

            string? conflict = FindDuplicateHotkeyConflict(
                quickEnabled ? quickNormalized : null,
                cycleEnabled ? cycleNormalized : null,
                cyclePrevEnabled ? cyclePrevNormalized : null,
                directSlotsEnabled ? directModsNormalized : null);

            if (conflict != null)
            {
                FluentDialogService.ShowWarning(
                    LocalizationService.Instance.GetString("Str_SwitcherSettings_DuplicateHotkeyTitle"),
                    LocalizationService.Instance.Format("Str_SwitcherSettings_DuplicateHotkeyMessageFormat", conflict),
                    owner: this);
                return;
            }

            _settings.AutoTagMultiInstanceWindowTitle = AutoTagToggle.IsChecked == true;
            _settings.RestoreAccountWindowBounds = RestoreAccountBoundsToggle.IsChecked == true;
            _settings.MultiInstanceWindowTitleTemplate = string.IsNullOrWhiteSpace(TitleTemplateBox.Text)
                ? Settings.DefaultMultiInstanceWindowTitleTemplate
                : TitleTemplateBox.Text.Trim();

            _settings.EnableQuickSwitcherHotkey = quickEnabled;
            _settings.QuickSwitcherHotkey = quickNormalized;

            _settings.EnableCycleWindowHotkey = cycleEnabled;
            _settings.CycleWindowHotkey = cycleNormalized;

            _settings.EnableCyclePreviousWindowHotkey = cyclePrevEnabled;
            _settings.CyclePreviousWindowHotkey = cyclePrevNormalized;

            _settings.EnableDirectSlotHotkeys = directSlotsEnabled;
            _settings.DirectSlotHotkeyModifier = directModsNormalized;

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
        catch (Exception ex)
        {
            Logger.ExceptionStatic("WindowSwitcherSettingsDialog.Save", ex);
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                this);
        }
    }

    private static string? FindDuplicateHotkeyConflict(
        string? quick,
        string? cycle,
        string? cyclePrev,
        string? directMods)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? candidate in new[] { quick, cycle, cyclePrev })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!seen.Add(candidate))
            {
                return candidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(directMods))
        {
            for (int slot = 1; slot <= 9; slot++)
            {
                string dKey = $"{directMods}+{slot}";
                string numKey = $"{directMods}+NumPad{slot}";
                if (seen.Contains(dKey)) return dKey;
                if (seen.Contains(numKey)) return numKey;
            }
        }

        return null;
    }
}
