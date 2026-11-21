using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.App.ViewModels;

public enum AccountCookieHealth
{
    Unknown,
    Verifying,
    Valid,
    Invalid
}

/// <summary>
/// Presentation wrapper for <see cref="RobloxAccount"/> that supports selection, inline note editing,
/// localized setting summaries, and never exposes the raw session cookie to UI bindings.
/// </summary>
public sealed class AccountItem : ViewModelBase
{
    private const string HexUnknown = "#FF9CA3AF";
    private const string HexVerifying = "#FF3B82F6";
    private const string HexValid = "#FF10B981";
    private const string HexInvalid = "#FFEF4444";

    private bool _isChecked;
    private AccountCookieHealth _cookieHealth = AccountCookieHealth.Unknown;
    private string? _cookieHealthDetail;

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

    public AccountCookieHealth CookieHealth
    {
        get => _cookieHealth;
        private set
        {
            if (SetProperty(ref _cookieHealth, value))
            {
                OnPropertyChanged(nameof(CookieStatusText));
                OnPropertyChanged(nameof(CookieStatusColorHex));
                OnPropertyChanged(nameof(CookieStatusToolTip));
            }
        }
    }

    public string? CookieHealthDetail
    {
        get => _cookieHealthDetail;
        private set
        {
            if (SetProperty(ref _cookieHealthDetail, value))
            {
                OnPropertyChanged(nameof(CookieStatusToolTip));
            }
        }
    }

    public string CookieStatusText
    {
        get
        {
            var loc = LocalizationService.Instance;
            return _cookieHealth switch
            {
                AccountCookieHealth.Verifying => loc.GetString("Str_Accounts_CookieVerifying"),
                AccountCookieHealth.Valid => loc.GetString("Str_Accounts_CookieValid"),
                AccountCookieHealth.Invalid => loc.GetString("Str_Accounts_CookieInvalid"),
                _ => loc.GetString("Str_Accounts_CookieUnknown")
            };
        }
    }

    public string CookieStatusColorHex => _cookieHealth switch
    {
        AccountCookieHealth.Verifying => HexVerifying,
        AccountCookieHealth.Valid => HexValid,
        AccountCookieHealth.Invalid => HexInvalid,
        _ => HexUnknown
    };

    public string CookieStatusToolTip =>
        !string.IsNullOrWhiteSpace(_cookieHealthDetail)
            ? _cookieHealthDetail
            : CookieStatusText;

    public void SetCookieHealth(AccountCookieHealth health, string? detail = null)
    {
        _cookieHealthDetail = detail;
        OnPropertyChanged(nameof(CookieHealthDetail));

        if (_cookieHealth != health)
        {
            _cookieHealth = health;
            OnPropertyChanged(nameof(CookieHealth));
        }

        OnPropertyChanged(nameof(CookieStatusText));
        OnPropertyChanged(nameof(CookieStatusColorHex));
        OnPropertyChanged(nameof(CookieStatusToolTip));
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
        OnPropertyChanged(nameof(CookieStatusText));
        OnPropertyChanged(nameof(CookieStatusColorHex));
        OnPropertyChanged(nameof(CookieStatusToolTip));
    }
}
