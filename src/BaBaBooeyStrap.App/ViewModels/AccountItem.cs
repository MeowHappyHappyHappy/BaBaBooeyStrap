using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Integrations.Window;

namespace BaBaBooeyStrap.App.ViewModels;

public enum AccountCookieHealth
{
    Unknown,
    Verifying,
    Valid,
    Invalid
}

public enum AccountRunState
{
    NotRunning,
    Launching,
    Running,
    Minimized
}

/// <summary>
/// Presentation wrapper for <see cref="RobloxAccount"/> that supports selection, inline note editing,
/// localized setting summaries, live running state, and never exposes the raw session cookie to UI bindings.
/// </summary>
public sealed class AccountItem : ViewModelBase
{
    private const string HexUnknown = "#FF9CA3AF";
    private const string HexVerifying = "#FF3B82F6";
    private const string HexValid = "#FF10B981";
    private const string HexInvalid = "#FFEF4444";

    private const string HexRunRunning = "#FF10B981";
    private const string HexRunMinimized = "#FFF59E0B";
    private const string HexRunLaunching = "#FF3B82F6";
    private const string HexRunIdle = "#FF9CA3AF";

    private bool _isChecked;
    private AccountCookieHealth _cookieHealth = AccountCookieHealth.Unknown;
    private string? _cookieHealthDetail;
    private bool _isLaunching;
    private AccountRunState _runState = AccountRunState.NotRunning;
    private int? _runningProcessId;
    private int? _runningSlotIndex;
    private int? _runningMonitorIndex;
    private bool _isRunningFocused;

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

    public AccountRunState RunState => _runState;
    public bool IsRunning => _runState is AccountRunState.Running or AccountRunState.Minimized;
    public int? RunningProcessId => _runningProcessId;
    public int? RunningSlotIndex => _runningSlotIndex;
    public int? RunningMonitorIndex => _runningMonitorIndex;
    public bool IsRunningFocused => _isRunningFocused;

    public string RunStatusText
    {
        get
        {
            var loc = LocalizationService.Instance;
            return _runState switch
            {
                AccountRunState.Running => loc.GetString("Str_Accounts_RunStateRunning"),
                AccountRunState.Minimized => loc.GetString("Str_Accounts_RunStateMinimized"),
                AccountRunState.Launching => loc.GetString("Str_Accounts_RunStateLaunching"),
                _ => loc.GetString("Str_Accounts_RunStateIdle")
            };
        }
    }

    public string RunStatusColorHex => _runState switch
    {
        AccountRunState.Running => HexRunRunning,
        AccountRunState.Minimized => HexRunMinimized,
        AccountRunState.Launching => HexRunLaunching,
        _ => HexRunIdle
    };

    public string RunStatusToolTip
    {
        get
        {
            var loc = LocalizationService.Instance;
            int slot = _runningSlotIndex ?? 0;
            int pid = _runningProcessId ?? 0;
            int monitor = Math.Max(1, _runningMonitorIndex ?? 1);

            return _runState switch
            {
                AccountRunState.Running when _isRunningFocused => loc.Format("Str_Accounts_RunStateFocusedTipFormat", slot, pid, monitor),
                AccountRunState.Running => loc.Format("Str_Accounts_RunStateRunningTipFormat", slot, pid, monitor),
                AccountRunState.Minimized => loc.Format("Str_Accounts_RunStateMinimizedTipFormat", slot, pid, monitor),
                AccountRunState.Launching => loc.GetString("Str_Accounts_RunStateLaunchingTip"),
                _ => loc.GetString("Str_Accounts_RunStateIdleTip")
            };
        }
    }

    public void UpdateRunningState(RunningRobloxInstance? instance)
    {
        int? nextPid = instance?.ProcessId;
        int? nextSlot = instance?.SlotIndex;
        int? nextMonitor = instance != null ? Math.Max(1, instance.MonitorIndex) : null;
        bool nextFocused = instance?.IsFocused ?? false;
        AccountRunState nextState = instance != null
            ? (instance.IsMinimized ? AccountRunState.Minimized : AccountRunState.Running)
            : (_isLaunching ? AccountRunState.Launching : AccountRunState.NotRunning);

        bool stateChanged = _runState != nextState;
        bool wasRunning = IsRunning;
        bool pidChanged = _runningProcessId != nextPid;
        bool slotChanged = _runningSlotIndex != nextSlot;
        bool monitorChanged = _runningMonitorIndex != nextMonitor;
        bool focusedChanged = _isRunningFocused != nextFocused;

        if (!stateChanged && !pidChanged && !slotChanged && !monitorChanged && !focusedChanged)
        {
            return;
        }

        _runState = nextState;
        _runningProcessId = nextPid;
        _runningSlotIndex = nextSlot;
        _runningMonitorIndex = nextMonitor;
        _isRunningFocused = nextFocused;

        if (stateChanged)
        {
            OnPropertyChanged(nameof(RunState));
            OnPropertyChanged(nameof(RunStatusText));
            OnPropertyChanged(nameof(RunStatusColorHex));
        }

        if (wasRunning != IsRunning)
        {
            OnPropertyChanged(nameof(IsRunning));
        }

        if (pidChanged) OnPropertyChanged(nameof(RunningProcessId));
        if (slotChanged) OnPropertyChanged(nameof(RunningSlotIndex));
        if (monitorChanged) OnPropertyChanged(nameof(RunningMonitorIndex));
        if (focusedChanged) OnPropertyChanged(nameof(IsRunningFocused));

        OnPropertyChanged(nameof(RunStatusToolTip));
    }

    public void SetLaunching(bool isLaunching)
    {
        if (_isLaunching == isLaunching)
        {
            return;
        }

        _isLaunching = isLaunching;
        AccountRunState nextState = _runningProcessId.HasValue
            ? _runState
            : (_isLaunching ? AccountRunState.Launching : AccountRunState.NotRunning);

        if (_runState != nextState)
        {
            bool wasRunning = IsRunning;
            _runState = nextState;
            OnPropertyChanged(nameof(RunState));
            if (wasRunning != IsRunning)
            {
                OnPropertyChanged(nameof(IsRunning));
            }
            OnPropertyChanged(nameof(RunStatusText));
            OnPropertyChanged(nameof(RunStatusColorHex));
            OnPropertyChanged(nameof(RunStatusToolTip));
        }
    }

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
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(RunStatusText));
        OnPropertyChanged(nameof(RunStatusColorHex));
        OnPropertyChanged(nameof(RunStatusToolTip));
    }
}
