using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.App.ViewModels;

/// <summary>
/// Presentation wrapper for <see cref="RobloxAccount"/> that supports selection, inline note editing,
/// localized setting summaries, and never exposes the raw session cookie to UI bindings.
/// </summary>
public sealed class AccountItem : ViewModelBase
{
    private bool _isChecked;

    public AccountItem(RobloxAccount account)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));
    }

    public RobloxAccount Account { get; private set; }

    public Guid Id => Account.Id;
    public string Username => Account.Username;
    public string DisplayName => Account.DisplayName;
    public long UserId => Account.UserId;

    public string Notes
    {
        get => string.IsNullOrWhiteSpace(Account.Notes) ? string.Empty : Account.Notes;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!string.Equals(Account.Notes, normalized, StringComparison.Ordinal))
            {
                Account.Notes = normalized;
                OnPropertyChanged();
            }
        }
    }

    public string SettingsSummary
    {
        get
        {
            var loc = LocalizationService.Instance;
            if (Account.RobloxSettings is null)
            {
                return loc.GetString("Str_Accounts_SettingsDefault");
            }

            return Account.RobloxSettings.ToSummaryString(
                defaultLabel: loc.GetString("Str_Accounts_SettingsDefault"),
                volumeFormat: loc.GetString("Str_Accounts_VolumeSummaryFormat"),
                qualityAutoLabel: loc.GetString("Str_Accounts_QualityAutoSummary"),
                qualityLevelFormat: loc.GetString("Str_Accounts_QualityLevelSummaryFormat"),
                fpsFormat: loc.GetString("Str_Accounts_FpsSummaryFormat"),
                fullscreenLabel: loc.GetString("Str_Accounts_DisplayFullscreen"),
                windowedLabel: loc.GetString("Str_Accounts_DisplayWindowed"),
                customCountFormat: loc.GetString("Str_Accounts_CustomOverridesSummaryFormat"));
        }
    }

    public string LastLaunchedDisplay =>
        Account.LastLaunchedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        ?? LocalizationService.Instance.GetString("Str_Accounts_NeverLaunched");

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    public void UpdateAccount(RobloxAccount updated)
    {
        Account = updated ?? throw new ArgumentNullException(nameof(updated));
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(UserId));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(SettingsSummary));
        OnPropertyChanged(nameof(LastLaunchedDisplay));
    }
}
