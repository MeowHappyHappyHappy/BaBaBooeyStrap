using System.Collections.ObjectModel;
using System.Windows;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.Models;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class AccountSettingsDialog : FluentWindow
{
    private readonly ObservableCollection<CustomSettingOverride> _customOverrides = new();

    public AccountSettingsDialog(string username, RobloxClientSettingsProfile? currentProfile)
    {
        InitializeComponent();

        var loc = LocalizationService.Instance;
        AccountTitleText.Text = loc.Format("Str_AccountSettings_HeaderFormat", username);

        QualityComboBox.ItemsSource = new[]
        {
            new KeyValuePair<int, string>(0, loc.GetString("Str_AccountSettings_QualityAuto")),
            new KeyValuePair<int, string>(1, loc.GetString("Str_AccountSettings_QualityLowest")),
            new KeyValuePair<int, string>(2, "2"),
            new KeyValuePair<int, string>(3, "3"),
            new KeyValuePair<int, string>(4, "4"),
            new KeyValuePair<int, string>(5, loc.GetString("Str_AccountSettings_QualityMedium")),
            new KeyValuePair<int, string>(6, "6"),
            new KeyValuePair<int, string>(7, "7"),
            new KeyValuePair<int, string>(8, "8"),
            new KeyValuePair<int, string>(9, "9"),
            new KeyValuePair<int, string>(10, loc.GetString("Str_AccountSettings_QualityHighest"))
        };
        QualityComboBox.DisplayMemberPath = "Value";
        QualityComboBox.SelectedValuePath = "Key";
        QualityComboBox.SelectedValue = 1;

        FpsComboBox.ItemsSource = new[] { "30", "60", "120", "144", "240" };
        FpsComboBox.Text = "60";

        FullscreenComboBox.ItemsSource = new[]
        {
            new KeyValuePair<bool, string>(false, loc.GetString("Str_AccountSettings_DisplayWindowed")),
            new KeyValuePair<bool, string>(true, loc.GetString("Str_AccountSettings_DisplayFullscreen"))
        };
        FullscreenComboBox.DisplayMemberPath = "Value";
        FullscreenComboBox.SelectedValuePath = "Key";
        FullscreenComboBox.SelectedValue = false;

        CustomOverridesGrid.ItemsSource = _customOverrides;

        LoadProfile(currentProfile);
    }

    public RobloxClientSettingsProfile ResultProfile { get; private set; } = new();

    private void LoadProfile(RobloxClientSettingsProfile? profile)
    {
        if (profile is null)
        {
            VolumeSlider.Value = 50;
            UpdateControlStates();
            return;
        }

        VolumeOverrideCheck.IsChecked = profile.MasterVolumePercent.HasValue;
        VolumeSlider.Value = profile.MasterVolumePercent ?? 50;
        VolumeValueText.Text = $"{(int)VolumeSlider.Value}%";

        QualityOverrideCheck.IsChecked = profile.QualityLevel.HasValue;
        QualityComboBox.SelectedValue = profile.QualityLevel ?? 1;

        FpsOverrideCheck.IsChecked = profile.FramerateCap.HasValue;
        FpsComboBox.Text = (profile.FramerateCap ?? 60).ToString();

        FullscreenOverrideCheck.IsChecked = profile.Fullscreen.HasValue;
        FullscreenComboBox.SelectedValue = profile.Fullscreen ?? false;

        _customOverrides.Clear();
        foreach (var item in profile.CustomOverrides)
        {
            _customOverrides.Add(new CustomSettingOverride { Key = item.Key, Value = item.Value });
        }

        UpdateControlStates();
    }

    private void UpdateControlStates()
    {
        VolumeSlider.IsEnabled = VolumeOverrideCheck.IsChecked == true;
        QualityComboBox.IsEnabled = QualityOverrideCheck.IsChecked == true;
        FpsComboBox.IsEnabled = FpsOverrideCheck.IsChecked == true;
        FullscreenComboBox.IsEnabled = FullscreenOverrideCheck.IsChecked == true;
    }

    private void OverrideCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateControlStates();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeValueText is not null)
        {
            VolumeValueText.Text = $"{(int)e.NewValue}%";
        }
    }

    private void ApplyAfkPresetButton_Click(object sender, RoutedEventArgs e)
    {
        VolumeOverrideCheck.IsChecked = true;
        VolumeSlider.Value = 0;

        QualityOverrideCheck.IsChecked = true;
        QualityComboBox.SelectedValue = 1;

        FpsOverrideCheck.IsChecked = true;
        FpsComboBox.Text = "30";

        FullscreenOverrideCheck.IsChecked = true;
        FullscreenComboBox.SelectedValue = false;

        UpdateControlStates();
    }

    private void ResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        VolumeOverrideCheck.IsChecked = false;
        QualityOverrideCheck.IsChecked = false;
        FpsOverrideCheck.IsChecked = false;
        FullscreenOverrideCheck.IsChecked = false;
        _customOverrides.Clear();
        UpdateControlStates();
    }

    private void AddCustomRowButton_Click(object sender, RoutedEventArgs e)
    {
        _customOverrides.Add(new CustomSettingOverride { Key = "VSync", Value = "false" });
    }

    private void RemoveCustomRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (CustomOverridesGrid.SelectedItem is CustomSettingOverride selected)
        {
            _customOverrides.Remove(selected);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        int? fpsCap = null;
        if (FpsOverrideCheck.IsChecked == true)
        {
            if (!int.TryParse(FpsComboBox.Text.Trim(), out var parsedFps) || parsedFps < 0)
            {
                FluentDialogService.ShowWarning(
                    LocalizationService.Instance.GetString("Str_AccountSettings_InvalidFpsTitle"),
                    LocalizationService.Instance.GetString("Str_AccountSettings_InvalidFpsMessage"),
                    owner: this);
                return;
            }

            fpsCap = parsedFps;
        }

        ResultProfile = new RobloxClientSettingsProfile
        {
            MasterVolumePercent = VolumeOverrideCheck.IsChecked == true ? (int)VolumeSlider.Value : null,
            QualityLevel = QualityOverrideCheck.IsChecked == true && QualityComboBox.SelectedValue is int q ? q : null,
            FramerateCap = fpsCap,
            Fullscreen = FullscreenOverrideCheck.IsChecked == true && FullscreenComboBox.SelectedValue is bool fs ? fs : null,
            CustomOverrides = _customOverrides
                .Where(o => !string.IsNullOrWhiteSpace(o.Key))
                .Select(o => new CustomSettingOverride { Key = o.Key.Trim(), Value = o.Value.Trim() })
                .ToList()
        };

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
