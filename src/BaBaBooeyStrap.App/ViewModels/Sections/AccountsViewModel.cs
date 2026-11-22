using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Data;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Launch;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.MultiInstance.Abstractions;

namespace BaBaBooeyStrap.App.ViewModels.Sections;

public sealed class AccountsViewModel : ViewModelBase
{
    private const string HexSuccess = "#FF10B981";
    private const string HexWarning = "#FFF59E0B";
    private const string HexDanger = "#FFEF4444";
    private const string HexMuted = "#FF9CA3AF";

    private readonly Settings _settings;
    private readonly IPaths _paths;
    private readonly IDpapiAccountStore? _accountStore;
    private readonly IJoinTargetStore _joinTargetStore;
    private readonly IMultiInstanceController? _multiInstanceController;
    private readonly IProcessSupervisor? _processSupervisor;
    private readonly IRobloxLauncher? _launcher;
    private readonly IGlobalSettingsManager _globalSettingsManager;
    private readonly IJsonManager<State>? _stateManager;
    private readonly IRobloxAuthService? _injectedAuthService;
    private readonly Action<string, object?[]> _setLocalizedStatus;
    private readonly StringBuilder _activityLogBuilder = new();
    private readonly ConcurrentDictionary<long, RobloxGameInfo?> _gameInfoCache = new();
    private CancellationTokenSource? _gameInfoCts;
    private string _resolvedGameName = string.Empty;
    private string _resolvedGamePlaceIdText = string.Empty;
    private string? _resolvedGameThumbnailUrl;
    private bool _hasResolvedGameInfo;
    private bool _isResolvingGameInfo;
    private long? _resolvedPlaceId;

    private RobloxAccount? _selectedAccount;
    private AccountItem? _selectedAccountItem;
    private SavedJoinTarget? _selectedSavedTarget;
    private string _accountSearchText = string.Empty;
    private string _targetName = string.Empty;
    private string _launchTargetInput = string.Empty;
    private string _launchJobId = string.Empty;
    private bool _isFollowUserTargetMode;
    private string _launchPlaceIdText = string.Empty;
    private string _launchShareLink = string.Empty;
    private string _multiInstanceStatusText = "Multi-Instance: Unknown";
    private string _runningPidsText = "Running clients: 0";
    private string _runningPidsToolTip = string.Empty;
    private bool _ownsMutex;
    private bool _waitingForMutex;
    private string _mutexBadgeText = string.Empty;
    private string _mutexToolTip = string.Empty;
    private string _mutexIndicatorHex = HexMuted;
    private bool _isProtocolRegistered;
    private string _protocolStatusText = string.Empty;
    private string _protocolToolTip = string.Empty;
    private string _protocolIndicatorHex = HexMuted;
    private bool _isLaunching;
    private bool _isVerifyingCookies;
    private CancellationTokenSource? _launchCts;
    private string? _lastLoggedMutexState;
    private string? _lastLoggedMutexError;

    public AccountsViewModel(
        Settings settings,
        IPaths paths,
        IDpapiAccountStore? accountStore,
        IJoinTargetStore joinTargetStore,
        IMultiInstanceController? multiInstanceController,
        IProcessSupervisor? processSupervisor,
        IRobloxLauncher? launcher,
        IGlobalSettingsManager globalSettingsManager,
        IJsonManager<State>? stateManager,
        Action<string, object?[]> setLocalizedStatus,
        IRobloxAuthService? authService = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _accountStore = accountStore;
        _joinTargetStore = joinTargetStore ?? throw new ArgumentNullException(nameof(joinTargetStore));
        _multiInstanceController = multiInstanceController;
        _processSupervisor = processSupervisor;
        _launcher = launcher;
        _globalSettingsManager = globalSettingsManager ?? throw new ArgumentNullException(nameof(globalSettingsManager));
        _stateManager = stateManager;
        _injectedAuthService = authService;
        _setLocalizedStatus = setLocalizedStatus ?? throw new ArgumentNullException(nameof(setLocalizedStatus));

        FilteredAccountItems = CollectionViewSource.GetDefaultView(AccountItems);
        FilteredAccountItems.Filter = FilterAccountItem;

        ReloadAccounts();
        ReloadSavedTargets();
        RestoreSavedTargetState();
        RefreshProtocolStatus();
        RefreshMultiInstanceStatus();
    }

    public TimeSpan WindowWaitTimeout { get; set; } = ProcessSupervisor.DefaultWindowWaitTimeout;

    public ObservableCollection<RobloxAccount> Accounts { get; } = new();
    public ObservableCollection<AccountItem> AccountItems { get; } = new();
    public ICollectionView FilteredAccountItems { get; }
    public ObservableCollection<SavedJoinTarget> SavedTargets { get; } = new();

    public event EventHandler? ActivityLogAppended;

    public bool MultiInstanceEnabled
    {
        get => _settings.MultiInstanceEnabled;
        set
        {
            if (_settings.MultiInstanceEnabled != value)
            {
                _settings.MultiInstanceEnabled = value;
                OnPropertyChanged();
                ApplyMultiInstanceSetting();
            }
        }
    }

    public int MultiInstanceLaunchDelayMs
    {
        get => _settings.MultiInstanceLaunchDelayMs;
        set
        {
            int clamped = Math.Max(500, value);
            if (_settings.MultiInstanceLaunchDelayMs != clamped)
            {
                _settings.MultiInstanceLaunchDelayMs = clamped;
                OnPropertyChanged();
            }
        }
    }

    public string MultiInstanceStatusText
    {
        get => _multiInstanceStatusText;
        private set => SetProperty(ref _multiInstanceStatusText, value);
    }

    public string RunningPidsText
    {
        get => _runningPidsText;
        private set => SetProperty(ref _runningPidsText, value);
    }

    public bool OwnsMutex
    {
        get => _ownsMutex;
        private set => SetProperty(ref _ownsMutex, value);
    }

    public bool WaitingForMutex
    {
        get => _waitingForMutex;
        private set => SetProperty(ref _waitingForMutex, value);
    }

    public string MutexBadgeText
    {
        get => _mutexBadgeText;
        private set => SetProperty(ref _mutexBadgeText, value);
    }

    public string MutexToolTip
    {
        get => _mutexToolTip;
        private set => SetProperty(ref _mutexToolTip, value);
    }

    public string MutexIndicatorHex
    {
        get => _mutexIndicatorHex;
        private set => SetProperty(ref _mutexIndicatorHex, value);
    }

    public string RunningPidsToolTip
    {
        get => _runningPidsToolTip;
        private set => SetProperty(ref _runningPidsToolTip, value);
    }

    public bool IsProtocolRegistered
    {
        get => _isProtocolRegistered;
        private set => SetProperty(ref _isProtocolRegistered, value);
    }

    public string ProtocolStatusText
    {
        get => _protocolStatusText;
        private set => SetProperty(ref _protocolStatusText, value);
    }

    public string ProtocolToolTip
    {
        get => _protocolToolTip;
        private set => SetProperty(ref _protocolToolTip, value);
    }

    public string ProtocolIndicatorHex
    {
        get => _protocolIndicatorHex;
        private set => SetProperty(ref _protocolIndicatorHex, value);
    }

    public RobloxAccount? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value))
            {
                var matchingItem = value is null
                    ? null
                    : AccountItems.FirstOrDefault(i => ReferenceEquals(i.Account, value) || i.Id == value.Id);
                if (!ReferenceEquals(_selectedAccountItem, matchingItem))
                {
                    _selectedAccountItem = matchingItem;
                    OnPropertyChanged(nameof(SelectedAccountItem));
                }
            }
        }
    }

    public AccountItem? SelectedAccountItem
    {
        get => _selectedAccountItem;
        set
        {
            if (SetProperty(ref _selectedAccountItem, value))
            {
                var account = value?.Account;
                if (!ReferenceEquals(_selectedAccount, account))
                {
                    _selectedAccount = account;
                    OnPropertyChanged(nameof(SelectedAccount));
                }
            }
        }
    }

    public string AccountSearchText
    {
        get => _accountSearchText;
        set
        {
            if (SetProperty(ref _accountSearchText, value ?? string.Empty))
            {
                FilteredAccountItems.Refresh();
                OnPropertyChanged(nameof(IsAccountReorderEnabled));
            }
        }
    }

    public string AccountCountText => $"({AccountItems.Count})";

    public SavedJoinTarget? SelectedSavedTarget
    {
        get => _selectedSavedTarget;
        set
        {
            if (SetProperty(ref _selectedSavedTarget, value) && value is not null)
            {
                TargetName = value.Name;
                LaunchTargetInput = value.TargetInput;
                LaunchJobId = value.JobId ?? string.Empty;
                try
                {
                    var parsed = RobloxLauncher.ParseTargetInput(value.TargetInput);
                    IsFollowUserTargetMode = parsed.Kind == LaunchTargetKind.FollowUser;
                }
                catch
                {
                    IsFollowUserTargetMode = false;
                }

                if (value.PlaceId is > 0 && !string.IsNullOrWhiteSpace(value.GameName))
                {
                    var info = new RobloxGameInfo(value.PlaceId.Value, 0, value.GameName, value.ThumbnailUrl);
                    if (!string.IsNullOrWhiteSpace(value.ThumbnailUrl))
                    {
                        _gameInfoCache.TryAdd(value.PlaceId.Value, info);
                    }
                    ApplyResolvedGameInfo(info);
                }
            }
        }
    }

    public bool IsFollowUserTargetMode
    {
        get => _isFollowUserTargetMode;
        set
        {
            if (SetProperty(ref _isFollowUserTargetMode, value))
            {
                OnPropertyChanged(nameof(TargetModeSelectedIndex));
                OnPropertyChanged(nameof(IsFollowUserModeSelected));
                OnPropertyChanged(nameof(IsGameServerTargetMode));
                OnPropertyChanged(nameof(IsJobIdInputVisible));
                OnPropertyChanged(nameof(TargetInputLabelText));
                OnPropertyChanged(nameof(TargetInputPlaceholderText));
                ScheduleResolveGameInfo(_launchTargetInput);
            }
        }
    }

    public int TargetModeSelectedIndex
    {
        get => _isFollowUserTargetMode ? 1 : 0;
        set => IsFollowUserTargetMode = value == 1;
    }

    public bool IsFollowUserModeSelected
    {
        get => _isFollowUserTargetMode;
        set
        {
            if (value)
            {
                IsFollowUserTargetMode = true;
            }
        }
    }

    public bool IsGameServerTargetMode
    {
        get => !_isFollowUserTargetMode;
        set
        {
            if (value)
            {
                IsFollowUserTargetMode = false;
            }
        }
    }

    public bool IsJobIdInputVisible => !_isFollowUserTargetMode;

    public string TargetInputLabelText => LocalizationService.Instance.GetString(
        _isFollowUserTargetMode ? "Str_Accounts_FollowUserInputLabel" : "Str_Accounts_TargetInputLabel");

    public string TargetInputPlaceholderText => LocalizationService.Instance.GetString(
        _isFollowUserTargetMode ? "Str_Accounts_FollowUserInputPlaceholder" : "Str_Accounts_TargetInputPlaceholder");

    public string TargetName
    {
        get => _targetName;
        set
        {
            if (SetProperty(ref _targetName, value ?? string.Empty) && _stateManager != null)
            {
                _stateManager.Prop.LastTargetName = _targetName.Trim();
            }
        }
    }

    public string LaunchTargetInput
    {
        get => _launchTargetInput;
        set
        {
            if (SetProperty(ref _launchTargetInput, value ?? string.Empty))
            {
                if (_stateManager != null)
                {
                    _stateManager.Prop.LastTargetInput = _launchTargetInput.Trim();
                }
                ScheduleResolveGameInfo(_launchTargetInput);
            }
        }
    }

    public string ResolvedGameName
    {
        get => _resolvedGameName;
        private set => SetProperty(ref _resolvedGameName, value);
    }

    public string ResolvedGamePlaceIdText
    {
        get => _resolvedGamePlaceIdText;
        private set => SetProperty(ref _resolvedGamePlaceIdText, value);
    }

    public string? ResolvedGameThumbnailUrl
    {
        get => _resolvedGameThumbnailUrl;
        private set => SetProperty(ref _resolvedGameThumbnailUrl, value);
    }

    public bool HasResolvedGameInfo
    {
        get => _hasResolvedGameInfo;
        private set => SetProperty(ref _hasResolvedGameInfo, value);
    }

    public bool IsResolvingGameInfo
    {
        get => _isResolvingGameInfo;
        private set => SetProperty(ref _isResolvingGameInfo, value);
    }

    public string LaunchJobId
    {
        get => _launchJobId;
        set
        {
            if (SetProperty(ref _launchJobId, value ?? string.Empty) && _stateManager != null)
            {
                _stateManager.Prop.LastJobId = _launchJobId.Trim();
            }
        }
    }

    public string LaunchPlaceIdText
    {
        get => _launchPlaceIdText;
        set => SetProperty(ref _launchPlaceIdText, value);
    }

    public string LaunchShareLink
    {
        get => _launchShareLink;
        set => SetProperty(ref _launchShareLink, value);
    }

    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (SetProperty(ref _isLaunching, value))
            {
                OnPropertyChanged(nameof(IsNotLaunching));
                OnPropertyChanged(nameof(CanCancelLaunch));
                OnPropertyChanged(nameof(IsAccountReorderEnabled));
            }
        }
    }

    public bool IsNotLaunching => !IsLaunching;
    public bool IsAccountReorderEnabled => IsNotLaunching && string.IsNullOrWhiteSpace(AccountSearchText);
    public bool CanCancelLaunch => IsLaunching && _launchCts != null;

    public bool IsVerifyingCookies
    {
        get => _isVerifyingCookies;
        private set
        {
            if (SetProperty(ref _isVerifyingCookies, value))
            {
                OnPropertyChanged(nameof(IsNotVerifyingCookies));
            }
        }
    }

    public bool IsNotVerifyingCookies => !IsVerifyingCookies;
    public string ActivityLogText => _activityLogBuilder.ToString();

    public void PersistLastTargetState()
    {
        if (_stateManager == null) return;
        _stateManager.Prop.LastTargetName = _targetName.Trim();
        _stateManager.Prop.LastTargetInput = _launchTargetInput.Trim();
        _stateManager.Prop.LastJobId = _launchJobId.Trim();
    }

    public void ReloadAccounts()
    {
        var checkedIds = AccountItems.Where(i => i.IsChecked).Select(i => i.Id).ToHashSet();
        var selectedId = _selectedAccountItem?.Id ?? _selectedAccount?.Id;

        Accounts.Clear();
        AccountItems.Clear();
        if (_accountStore != null)
        {
            try
            {
                var loaded = _accountStore.LoadAll();
                foreach (var acc in loaded)
                {
                    Accounts.Add(acc);
                    var item = new AccountItem(acc)
                    {
                        IsChecked = checkedIds.Contains(acc.Id)
                    };
                    AccountItems.Add(item);
                }
            }
            catch
            {
                // Corrupted DPAPI store renders empty list safely
            }
        }

        if (selectedId.HasValue)
        {
            SelectedAccountItem = AccountItems.FirstOrDefault(i => i.Id == selectedId.Value);
        }
        else if (_selectedAccountItem != null || _selectedAccount != null)
        {
            SelectedAccountItem = null;
        }

        FilteredAccountItems.Refresh();
        OnPropertyChanged(nameof(AccountCountText));
    }

    public void ReloadSavedTargets()
    {
        SavedTargets.Clear();
        try
        {
            foreach (var target in _joinTargetStore.LoadAll())
            {
                SavedTargets.Add(target);
            }
        }
        catch
        {
            // Corrupted target store renders empty list safely
        }
    }

    private void RestoreSavedTargetState()
    {
        if (_stateManager == null) return;

        if (!string.IsNullOrWhiteSpace(_stateManager.Prop.LastTargetName))
        {
            _targetName = _stateManager.Prop.LastTargetName;
            OnPropertyChanged(nameof(TargetName));
        }

        if (!string.IsNullOrWhiteSpace(_stateManager.Prop.LastTargetInput))
        {
            _launchTargetInput = _stateManager.Prop.LastTargetInput;
            OnPropertyChanged(nameof(LaunchTargetInput));
            try
            {
                var parsed = RobloxLauncher.ParseTargetInput(_launchTargetInput);
                if (parsed.Kind == LaunchTargetKind.FollowUser)
                {
                    IsFollowUserTargetMode = true;
                }
            }
            catch { }
            ScheduleResolveGameInfo(_launchTargetInput);
        }

        if (!string.IsNullOrWhiteSpace(_stateManager.Prop.LastJobId))
        {
            _launchJobId = _stateManager.Prop.LastJobId;
            OnPropertyChanged(nameof(LaunchJobId));
        }
    }

    private bool FilterAccountItem(object obj)
    {
        if (obj is not AccountItem item) return false;

        var query = _accountSearchText.Trim();
        if (string.IsNullOrWhiteSpace(query)) return true;

        return item.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.UserId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Notes.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public void RefreshLocalizedProperties()
    {
        foreach (var item in AccountItems)
        {
            item.Refresh();
        }
        RefreshProtocolStatus();
        RefreshMultiInstanceStatus();
        OnPropertyChanged(nameof(AccountCountText));
        OnPropertyChanged(nameof(TargetInputLabelText));
        OnPropertyChanged(nameof(TargetInputPlaceholderText));
    }

    public void RefreshProtocolStatus()
    {
        try
        {
            bool registered = RobloxLauncher.IsRobloxProtocolRegistered();
            IsProtocolRegistered = registered;
            ProtocolIndicatorHex = registered ? HexSuccess : HexDanger;
            ProtocolStatusText = LocalizationService.Instance.GetString(
                registered ? "Str_Accounts_ProtocolRegistered" : "Str_Accounts_ProtocolMissing");
            ProtocolToolTip = LocalizationService.Instance.GetString(
                registered ? "Str_Accounts_ProtocolRegisteredTip" : "Str_Accounts_ProtocolMissingTip");
        }
        catch (Exception ex)
        {
            IsProtocolRegistered = false;
            ProtocolIndicatorHex = HexWarning;
            ProtocolStatusText = LocalizationService.Instance.GetString("Str_Accounts_ProtocolUnknown");
            ProtocolToolTip = ex.Message;
        }
    }

    public void RefreshMultiInstanceStatus()
    {
        if (_multiInstanceController != null)
        {
            var status = _multiInstanceController.GetStatus();
            OwnsMutex = status.OwnsMutex;
            WaitingForMutex = status.WaitingForMutex;

            string mutexLabel = status.OwnsMutex
                ? LocalizationService.Instance.GetString("Str_Accounts_MutexHeldActive")
                : (status.WaitingForMutex
                    ? LocalizationService.Instance.GetString("Str_Accounts_MutexBlocked")
                    : (status.Enabled
                        ? LocalizationService.Instance.GetString("Str_Accounts_MutexNotAcquired")
                        : LocalizationService.Instance.GetString("Str_Common_Disabled")));
            MultiInstanceStatusText = LocalizationService.Instance.Format("Str_Accounts_MultiInstanceStatusFormat", mutexLabel);
            RunningPidsText = LocalizationService.Instance.Format("Str_Accounts_RunningClientsFormat", status.RunningRobloxPids.Count);
            RunningPidsToolTip = status.RunningRobloxPids.Count > 0
                ? LocalizationService.Instance.Format("Str_Accounts_RunningPidsTipFormat", string.Join(", ", status.RunningRobloxPids))
                : LocalizationService.Instance.GetString("Str_Accounts_NoRunningPidsTip");

            string badgeLabel;
            string badgeHex;
            if (!status.Enabled)
            {
                badgeLabel = LocalizationService.Instance.GetString("Str_Accounts_MutexLabelDisabled");
                badgeHex = HexMuted;
            }
            else if (status.OwnsMutex)
            {
                badgeLabel = LocalizationService.Instance.GetString("Str_Accounts_MutexLabelHeld");
                badgeHex = HexSuccess;
            }
            else if (status.WaitingForMutex)
            {
                badgeLabel = LocalizationService.Instance.GetString("Str_Accounts_MutexLabelWaiting");
                badgeHex = HexWarning;
            }
            else
            {
                badgeLabel = LocalizationService.Instance.GetString("Str_Accounts_MutexLabelEnabled");
                badgeHex = HexWarning;
            }

            MutexBadgeText = badgeLabel;
            MutexIndicatorHex = badgeHex;
            MutexToolTip = string.IsNullOrWhiteSpace(status.LastError)
                ? badgeLabel
                : LocalizationService.Instance.Format("Str_Accounts_MutexLabelErrorFormat", badgeLabel, status.LastError);

            bool stateChanged = !string.Equals(_lastLoggedMutexState, badgeLabel, StringComparison.Ordinal);
            if (stateChanged)
            {
                if (_lastLoggedMutexState is not null)
                {
                    AppendActivityLog(FormatMultiInstanceStatusLog(
                        LocalizationService.Instance.Format("Str_Accounts_LogMutexChangedFormat", badgeLabel),
                        status));
                }

                _lastLoggedMutexState = badgeLabel;
            }

            if (!string.IsNullOrWhiteSpace(status.LastError) &&
                !string.Equals(_lastLoggedMutexError, status.LastError, StringComparison.Ordinal))
            {
                if (!stateChanged)
                {
                    AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogMutexErrorFormat", status.LastError));
                }
            }

            _lastLoggedMutexError = status.LastError;
        }
        else
        {
            var pids = _processSupervisor?.GetRunningRobloxPids() ?? Array.Empty<int>();
            OwnsMutex = false;
            WaitingForMutex = false;
            string stateLabel = LocalizationService.Instance.GetString(_settings.MultiInstanceEnabled ? "Str_Common_Enabled" : "Str_Common_Disabled");
            MultiInstanceStatusText = LocalizationService.Instance.Format("Str_Accounts_MultiInstanceStatusFormat", stateLabel);
            RunningPidsText = LocalizationService.Instance.Format("Str_Accounts_RunningClientsFormat", pids.Count);
            RunningPidsToolTip = pids.Count > 0
                ? LocalizationService.Instance.Format("Str_Accounts_RunningPidsTipFormat", string.Join(", ", pids))
                : LocalizationService.Instance.GetString("Str_Accounts_NoRunningPidsTip");
            MutexBadgeText = stateLabel;
            MutexIndicatorHex = _settings.MultiInstanceEnabled ? HexWarning : HexMuted;
            MutexToolTip = stateLabel;
        }
    }

    private void ApplyMultiInstanceSetting()
    {
        if (_multiInstanceController == null) return;
        if (_settings.MultiInstanceEnabled)
        {
            var status = _multiInstanceController.Enable();
            _lastLoggedMutexError = status.LastError;
            AppendActivityLog(FormatMultiInstanceStatusLog(
                LocalizationService.Instance.GetString("Str_Accounts_LogMutexEnabledPrefix"),
                status));
        }
        else
        {
            _multiInstanceController.Disable();
            AppendActivityLog(LocalizationService.Instance.GetString("Str_Accounts_LogMutexDisabled"));
        }

        RefreshMultiInstanceStatus();
    }

    public void AddOrUpdateAccount(RobloxAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var existingItem = AccountItems.FirstOrDefault(a =>
            (account.UserId > 0 && a.UserId == account.UserId) || a.Id == account.Id);

        if (existingItem != null)
        {
            var existing = existingItem.Account;
            existing.Username = account.Username;
            existing.DisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName;
            existing.UserId = account.UserId;
            existing.Cookie = account.Cookie;
            if (!string.IsNullOrWhiteSpace(account.Notes))
            {
                existing.Notes = account.Notes;
            }
            if (account.RobloxSettings != null)
            {
                existing.RobloxSettings = account.RobloxSettings;
            }

            _accountStore?.Upsert(existing);
            existingItem.Refresh();
            SelectedAccountItem = existingItem;
            _setLocalizedStatus("Str_Accounts_StatusAdded", [existing.Username]);
            AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_StatusAdded", existing.Username));
            return;
        }

        _accountStore?.Upsert(account);
        if (_accountStore != null)
        {
            ReloadAccounts();
            SelectedAccountItem = AccountItems.FirstOrDefault(a =>
                (account.UserId > 0 && a.UserId == account.UserId) || a.Id == account.Id);
        }
        else
        {
            Accounts.Add(account);
            var newItem = new AccountItem(account);
            AccountItems.Add(newItem);
            SelectedAccountItem = newItem;
            FilteredAccountItems.Refresh();
            OnPropertyChanged(nameof(AccountCountText));
        }

        _setLocalizedStatus("Str_Accounts_StatusAdded", [account.Username]);
        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_StatusAdded", account.Username));
    }

    public void RemoveSelectedAccount()
    {
        if (_selectedAccount == null) return;
        _accountStore?.Delete(_selectedAccount.Id);
        string name = _selectedAccount.Username;
        if (_accountStore != null)
        {
            ReloadAccounts();
        }
        else
        {
            var item = AccountItems.FirstOrDefault(i => i.Id == _selectedAccount.Id);
            if (item != null) AccountItems.Remove(item);
            Accounts.Remove(_selectedAccount);
            SelectedAccountItem = null;
            OnPropertyChanged(nameof(AccountCountText));
        }
        _setLocalizedStatus("Str_Accounts_StatusRemoved", [name]);
    }

    public IReadOnlyList<AccountItem> GetCheckedAccounts() =>
        AccountItems.Where(a => a.IsChecked).ToList();

    public IReadOnlyList<AccountItem> GetRemovalCandidates()
    {
        var checkedItems = AccountItems.Where(a => a.IsChecked).ToList();
        if (checkedItems.Count > 0)
        {
            return checkedItems;
        }

        return SelectedAccountItem != null ? [SelectedAccountItem] : Array.Empty<AccountItem>();
    }

    public int RemoveCheckedOrSelectedAccounts()
    {
        if (IsLaunching) return 0;

        var toRemove = GetRemovalCandidates().ToList();

        if (toRemove.Count == 0)
        {
            _setLocalizedStatus("Str_Accounts_StatusSelectToRemove", Array.Empty<object?>());
            return 0;
        }

        int removedCount = 0;
        foreach (var item in toRemove)
        {
            if (_accountStore == null || _accountStore.Delete(item.Id))
            {
                removedCount++;
            }

            Accounts.Remove(item.Account);
            AccountItems.Remove(item);
        }

        if (SelectedAccountItem != null && toRemove.Contains(SelectedAccountItem))
        {
            SelectedAccountItem = null;
        }

        FilteredAccountItems.Refresh();
        OnPropertyChanged(nameof(AccountCountText));

        _setLocalizedStatus("Str_Accounts_StatusRemovedCountFormat", [removedCount]);
        AppendActivityLog(LocalizationService.Instance.Format(
            "Str_Accounts_LogRemovedCountFormat",
            removedCount,
            string.Join(", ", toRemove.Select(a => a.Username))));
        return removedCount;
    }

    public void ToggleSelectAllAccounts()
    {
        var visibleItems = FilteredAccountItems.Cast<AccountItem>().ToList();
        if (visibleItems.Count == 0) return;

        bool allChecked = visibleItems.All(a => a.IsChecked);
        foreach (var item in visibleItems)
        {
            item.IsChecked = !allChecked;
        }
    }

    public async Task<(int ValidCount, int InvalidCount)> VerifyAccountsAsync(
        IReadOnlyList<AccountItem>? targets = null,
        Func<string, CancellationToken, Task<bool>>? verifierOverride = null,
        CancellationToken ct = default)
    {
        if (IsVerifyingCookies) return (0, 0);

        List<AccountItem> targetList;
        if (targets != null)
        {
            targetList = targets.ToList();
        }
        else
        {
            var checkedItems = AccountItems.Where(a => a.IsChecked).ToList();
            targetList = checkedItems.Count > 0
                ? checkedItems
                : FilteredAccountItems.Cast<AccountItem>().ToList();
        }

        if (targetList.Count == 0)
        {
            _setLocalizedStatus("Str_Accounts_StatusNoAccountsToVerify", Array.Empty<object?>());
            return (0, 0);
        }

        IsVerifyingCookies = true;
        _setLocalizedStatus("Str_Accounts_StatusVerifyingCookiesFormat", [targetList.Count]);
        foreach (var item in targetList)
        {
            item.SetCookieHealth(AccountCookieHealth.Verifying);
        }

        int validCount = 0;
        int invalidCount = 0;
        object storeLock = new();
        using var semaphore = new SemaphoreSlim(3, 3);
        using var authService = verifierOverride == null ? new RobloxAuthService() : null;

        try
        {
            var tasks = targetList.Select(async item =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    string cookie = item.Account.Cookie;
                    if (string.IsNullOrWhiteSpace(cookie))
                    {
                        item.SetCookieHealth(
                            AccountCookieHealth.Invalid,
                            LocalizationService.Instance.GetString("Str_Accounts_CookieEmptyDetail"));
                        Interlocked.Increment(ref invalidCount);
                        return;
                    }

                    if (verifierOverride != null)
                    {
                        bool ok = await verifierOverride(cookie, ct);
                        if (ok)
                        {
                            item.SetCookieHealth(AccountCookieHealth.Valid);
                            Interlocked.Increment(ref validCount);
                        }
                        else
                        {
                            item.SetCookieHealth(AccountCookieHealth.Invalid);
                            Interlocked.Increment(ref invalidCount);
                        }
                    }
                    else
                    {
                        var user = await authService!.GetAuthenticatedUserAsync(cookie, ct);
                        item.Account.UserId = user.Id;
                        item.Account.Username = user.Name;
                        item.Account.DisplayName = user.DisplayName;
                        lock (storeLock)
                        {
                            _accountStore?.Upsert(item.Account);
                        }
                        item.Refresh();
                        item.SetCookieHealth(AccountCookieHealth.Valid, $"{user.DisplayName} (@{user.Name})");
                        Interlocked.Increment(ref validCount);
                    }
                }
                catch (OperationCanceledException)
                {
                    item.SetCookieHealth(AccountCookieHealth.Unknown);
                    throw;
                }
                catch (Exception ex)
                {
                    item.SetCookieHealth(AccountCookieHealth.Invalid, ex.Message);
                    Interlocked.Increment(ref invalidCount);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            _setLocalizedStatus("Str_Accounts_StatusVerifiedCookiesFormat", [validCount, invalidCount]);
            AppendActivityLog(LocalizationService.Instance.Format(
                "Str_Accounts_LogVerifiedCookiesFormat",
                validCount,
                invalidCount));
        }
        catch (OperationCanceledException)
        {
            _setLocalizedStatus("Str_Accounts_StatusVerifyCancelled", Array.Empty<object?>());
        }
        finally
        {
            IsVerifyingCookies = false;
        }

        return (validCount, invalidCount);
    }

    public bool MoveSelectedAccount(int delta)
    {
        if (IsLaunching || SelectedAccountItem is not AccountItem selected) return false;

        int index = AccountItems.IndexOf(selected);
        int targetIndex = index + delta;
        if (index < 0 || targetIndex < 0 || targetIndex >= AccountItems.Count) return false;

        AccountItems.Move(index, targetIndex);
        int accIndex = Accounts.IndexOf(selected.Account);
        if (accIndex >= 0 && accIndex != targetIndex && targetIndex < Accounts.Count)
        {
            Accounts.Move(accIndex, targetIndex);
        }
        else if (accIndex >= 0)
        {
            Accounts.Move(accIndex, Math.Clamp(targetIndex, 0, Accounts.Count - 1));
        }

        SelectedAccountItem = selected;
        FilteredAccountItems.Refresh();
        _accountStore?.SaveAll(AccountItems.Select(a => a.Account));
        return true;
    }

    public void CommitAccountNotesEdit(AccountItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            _accountStore?.Upsert(item.Account);
            _setLocalizedStatus("Str_Accounts_StatusUpdatedNotesFormat", [item.Username]);
        }
        catch (Exception ex)
        {
            AppendActivityLog($"Notes save failed: {ex.Message}");
        }
    }

    public int ApplyBatchAfkPreset()
    {
        if (IsLaunching) return 0;

        var targets = AccountItems.Where(a => a.IsChecked).ToList();
        if (targets.Count == 0 && SelectedAccountItem is AccountItem selected)
        {
            targets.Add(selected);
        }

        if (targets.Count == 0)
        {
            _setLocalizedStatus("Str_Accounts_StatusSelectForAfk", Array.Empty<object?>());
            return 0;
        }

        foreach (var item in targets)
        {
            item.Account.RobloxSettings = RobloxClientSettingsProfile.CreateAfkLowPerfPreset();
            _accountStore?.Upsert(item.Account);
            item.Refresh();
        }

        _setLocalizedStatus("Str_Accounts_StatusBatchAfkAppliedFormat", [targets.Count]);
        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogBatchAfkAppliedFormat", targets.Count));
        return targets.Count;
    }

    public void UpdateAccountSettings(AccountItem item, RobloxClientSettingsProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.Account.RobloxSettings = profile is { HasOverrides: true }
            ? profile.Clone()
            : null;
        _accountStore?.Upsert(item.Account);
        item.Refresh();

        _setLocalizedStatus("Str_Accounts_StatusSavedAccountSettingsFormat", [item.Username, item.SettingsSummary]);
        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogSavedAccountSettingsFormat", item.Username, item.SettingsSummary));
    }

    public bool SaveCurrentJoinTarget()
    {
        string name = TargetName.Trim();
        string targetInput = LaunchTargetInput.Trim();
        string? jobId = string.IsNullOrWhiteSpace(LaunchJobId) ? null : LaunchJobId.Trim();

        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(ResolvedGameName))
        {
            name = ResolvedGameName.Trim();
            TargetName = name;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            _setLocalizedStatus("Str_Accounts_StatusSelectTargetName", Array.Empty<object?>());
            return false;
        }

        string savedTargetInput;
        string? savedJobId;
        try
        {
            if (_isFollowUserTargetMode)
            {
                savedTargetInput = RobloxLauncher.BuildEffectiveTargetInput(targetInput, null, followUserMode: true);
                savedJobId = null;
            }
            else
            {
                var parsed = RobloxLauncher.ParseTargetInput(targetInput, followUserMode: false);
                savedTargetInput = targetInput;
                savedJobId = parsed.Kind == LaunchTargetKind.FollowUser ? null : jobId;
            }
        }
        catch (Exception ex)
        {
            _setLocalizedStatus("Str_Accounts_StatusInvalidTarget", Array.Empty<object?>());
            AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogSaveTargetFailedFormat", ex.Message));
            return false;
        }

        var existing = SavedTargets.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        SavedJoinTarget saved;
        if (existing != null)
        {
            existing.Name = name;
            existing.TargetInput = savedTargetInput;
            existing.JobId = savedJobId;
            existing.PlaceId = _resolvedPlaceId ?? existing.PlaceId;
            existing.GameName = !string.IsNullOrWhiteSpace(_resolvedGameName) ? _resolvedGameName : existing.GameName;
            existing.ThumbnailUrl = !string.IsNullOrWhiteSpace(_resolvedGameThumbnailUrl) ? _resolvedGameThumbnailUrl : existing.ThumbnailUrl;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            saved = existing;
            int idx = SavedTargets.IndexOf(existing);
            if (idx >= 0)
            {
                SavedTargets[idx] = existing;
            }
        }
        else
        {
            saved = new SavedJoinTarget
            {
                Name = name,
                TargetInput = savedTargetInput,
                JobId = savedJobId,
                PlaceId = _resolvedPlaceId,
                GameName = string.IsNullOrWhiteSpace(_resolvedGameName) ? null : _resolvedGameName,
                ThumbnailUrl = string.IsNullOrWhiteSpace(_resolvedGameThumbnailUrl) ? null : _resolvedGameThumbnailUrl,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            SavedTargets.Add(saved);
        }

        _joinTargetStore.Upsert(saved);
        SelectedSavedTarget = saved;

        _setLocalizedStatus("Str_Accounts_StatusSavedTargetFormat", [saved.Name]);
        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogSavedTargetFormat", saved.Name, saved.TargetInput));
        return true;
    }

    internal void ScheduleResolveGameInfo(string rawInput)
    {
        _gameInfoCts?.Cancel();
        _gameInfoCts?.Dispose();
        _gameInfoCts = null;

        if (_isFollowUserTargetMode || string.IsNullOrWhiteSpace(rawInput))
        {
            ClearResolvedGameInfo();
            return;
        }

        long? placeId = null;
        try
        {
            var parsed = RobloxLauncher.ParseTargetInput(rawInput.Trim(), followUserMode: false);
            if (parsed.Kind != LaunchTargetKind.FollowUser && parsed.PlaceId is > 0)
            {
                placeId = parsed.PlaceId.Value;
            }
        }
        catch
        {
            placeId = null;
        }

        if (!placeId.HasValue)
        {
            ClearResolvedGameInfo();
            return;
        }

        if (_gameInfoCache.TryGetValue(placeId.Value, out var cached))
        {
            ApplyResolvedGameInfo(cached);
            return;
        }

        var cts = new CancellationTokenSource();
        _gameInfoCts = cts;
        var syncContext = SynchronizationContext.Current;
        _ = ResolveGameInfoDebouncedAsync(placeId.Value, syncContext, cts.Token);
    }

    private async Task ResolveGameInfoDebouncedAsync(long placeId, SynchronizationContext? syncContext, CancellationToken ct)
    {
        void PostToCaller(Action action)
        {
            if (syncContext != null)
            {
                syncContext.Post(_ => action(), null);
            }
            else
            {
                action();
            }
        }

        try
        {
            await Task.Delay(350, ct).ConfigureAwait(false);
            PostToCaller(() => IsResolvingGameInfo = true);

            RobloxGameInfo? info;
            if (_injectedAuthService != null)
            {
                info = await _injectedAuthService.GetGameInfoByPlaceIdAsync(placeId, ct).ConfigureAwait(false);
            }
            else
            {
                using var auth = new RobloxAuthService();
                info = await auth.GetGameInfoByPlaceIdAsync(placeId, ct).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) return;

            if (info != null)
            {
                _gameInfoCache[placeId] = info;
            }

            PostToCaller(() =>
            {
                if (!ct.IsCancellationRequested)
                {
                    ApplyResolvedGameInfo(info);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            PostToCaller(() =>
            {
                if (!ct.IsCancellationRequested)
                {
                    ClearResolvedGameInfo();
                }
            });
        }
        finally
        {
            PostToCaller(() => IsResolvingGameInfo = false);
        }
    }

    private void ApplyResolvedGameInfo(RobloxGameInfo? info)
    {
        if (info == null)
        {
            ClearResolvedGameInfo();
            return;
        }

        _resolvedPlaceId = info.PlaceId;
        ResolvedGameName = info.Name;
        ResolvedGamePlaceIdText = $"Place ID: {info.PlaceId}";
        ResolvedGameThumbnailUrl = info.ThumbnailUrl;
        HasResolvedGameInfo = true;
    }

    private void ClearResolvedGameInfo()
    {
        _resolvedPlaceId = null;
        ResolvedGameName = string.Empty;
        ResolvedGamePlaceIdText = string.Empty;
        ResolvedGameThumbnailUrl = null;
        HasResolvedGameInfo = false;
        IsResolvingGameInfo = false;
    }

    public bool DeleteSelectedJoinTarget()
    {
        if (SelectedSavedTarget is not SavedJoinTarget selected)
        {
            _setLocalizedStatus("Str_Accounts_StatusSelectTargetToDelete", Array.Empty<object?>());
            return false;
        }

        _joinTargetStore.Delete(selected.Id);
        string deletedName = selected.Name;
        SavedTargets.Remove(selected);
        SelectedSavedTarget = null;

        _setLocalizedStatus("Str_Accounts_StatusDeletedTargetFormat", [deletedName]);
        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogDeletedTargetFormat", deletedName));
        return true;
    }

    public (int ImportedAccounts, int ImportedTargets) ImportFromMultiRoblox(string? multiRobloxDataDir = null)
    {
        string sourceDir = string.IsNullOrWhiteSpace(multiRobloxDataDir)
            ? Path.Combine(_paths.LocalAppData, "MultiRoblox")
            : multiRobloxDataDir;

        int importedAccounts = 0;
        int importedTargets = 0;

        try
        {
            string accountsPath = Path.Combine(sourceDir, "accounts.dat");
            if (File.Exists(accountsPath))
            {
                var sourceStore = new DpapiAccountStore(accountsPath);
                var existingUserIds = AccountItems.Where(a => a.UserId > 0).Select(a => a.UserId).ToHashSet();
                var existingIds = AccountItems.Select(a => a.Id).ToHashSet();

                foreach (var src in sourceStore.LoadAll())
                {
                    if ((src.UserId > 0 && existingUserIds.Contains(src.UserId)) || existingIds.Contains(src.Id))
                    {
                        continue;
                    }

                    var imported = new RobloxAccount
                    {
                        Id = src.Id,
                        Username = src.Username,
                        DisplayName = string.IsNullOrWhiteSpace(src.DisplayName) ? src.Username : src.DisplayName,
                        UserId = src.UserId,
                        Cookie = src.Cookie,
                        Notes = src.Notes,
                        CreatedAt = src.CreatedAt,
                        LastLaunchedAt = src.LastLaunchedAt,
                        RobloxSettings = src.RobloxSettings?.Clone()
                    };

                    _accountStore?.Upsert(imported);
                    Accounts.Add(imported);
                    AccountItems.Add(new AccountItem(imported));
                    if (imported.UserId > 0) existingUserIds.Add(imported.UserId);
                    existingIds.Add(imported.Id);
                    importedAccounts++;
                }

                if (importedAccounts > 0)
                {
                    FilteredAccountItems.Refresh();
                    OnPropertyChanged(nameof(AccountCountText));
                }
            }

            string targetsPath = Path.Combine(sourceDir, "targets.json");
            if (File.Exists(targetsPath))
            {
                var sourceTargets = new JoinTargetStore(targetsPath).LoadAll();
                var existingNames = SavedTargets.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var existingIds = SavedTargets.Select(t => t.Id).ToHashSet();

                foreach (var target in sourceTargets)
                {
                    if (existingNames.Contains(target.Name) || existingIds.Contains(target.Id))
                    {
                        continue;
                    }

                    var importedTarget = new SavedJoinTarget
                    {
                        Id = target.Id,
                        Name = target.Name,
                        TargetInput = target.TargetInput,
                        JobId = target.JobId,
                        UpdatedAt = target.UpdatedAt
                    };
                    _joinTargetStore.Upsert(importedTarget);
                    SavedTargets.Add(importedTarget);
                    existingNames.Add(importedTarget.Name);
                    existingIds.Add(importedTarget.Id);
                    importedTargets++;
                }
            }

            if (importedAccounts > 0 || importedTargets > 0)
            {
                _setLocalizedStatus("Str_Accounts_StatusImportSuccessFormat", [importedAccounts, importedTargets]);
                AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_StatusImportSuccessFormat", importedAccounts, importedTargets));
            }
            else
            {
                _setLocalizedStatus("Str_Accounts_StatusImportNone", Array.Empty<object?>());
            }
        }
        catch (Exception ex)
        {
            _setLocalizedStatus("Str_Accounts_StatusImportNone", Array.Empty<object?>());
            AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogImportFailedFormat", ex.Message));
        }

        return (importedAccounts, importedTargets);
    }

    public async Task<LaunchResult?> LaunchSelectedAccountAsync(CancellationToken ct = default)
    {
        if (_launcher == null || IsLaunching) return null;

        AccountItem? targetItem = SelectedAccountItem
            ?? (_selectedAccount != null
                ? AccountItems.FirstOrDefault(i => ReferenceEquals(i.Account, _selectedAccount) || i.Id == _selectedAccount.Id)
                  ?? new AccountItem(_selectedAccount)
                : null);

        if (targetItem == null)
        {
            var settings = new LaunchSettings();
            return await _launcher.LaunchPlayerAsync(settings, ct);
        }

        var batchResult = await LaunchAccountsSequentiallyCoreAsync([targetItem], ct);
        return batchResult.LastResult;
    }

    public async Task<LaunchResult?> LaunchSelectedFromUiAsync(CancellationToken ct = default)
    {
        if (IsLaunching) return null;

        if (SelectedAccountItem is not AccountItem selected)
        {
            _setLocalizedStatus("Str_Accounts_StatusSelectToLaunch", Array.Empty<object?>());
            AppendActivityLog(LocalizationService.Instance.GetString("Str_Accounts_LogCancelledNoSelection"));
            return null;
        }

        var batchResult = await LaunchAccountsSequentiallyCoreAsync([selected], ct);
        return batchResult.LastResult;
    }

    public async Task<int> LaunchCheckedAccountsAsync(CancellationToken ct = default)
    {
        if (IsLaunching) return 0;

        var checkedAccounts = AccountItems.Where(a => a.IsChecked).ToList();
        if (checkedAccounts.Count == 0)
        {
            _setLocalizedStatus("Str_Accounts_StatusCheckToLaunch", Array.Empty<object?>());
            AppendActivityLog(LocalizationService.Instance.GetString("Str_Accounts_LogCancelledNoChecked"));
            return 0;
        }

        var batchResult = await LaunchAccountsSequentiallyCoreAsync(checkedAccounts, ct);
        return batchResult.SuccessCount;
    }

    public async Task<int> LaunchAccountsSequentiallyAsync(IReadOnlyList<AccountItem> targets, CancellationToken ct = default)
    {
        var result = await LaunchAccountsSequentiallyCoreAsync(targets, ct).ConfigureAwait(false);
        return result.SuccessCount;
    }

    private async Task<(int SuccessCount, LaunchResult? LastResult)> LaunchAccountsSequentiallyCoreAsync(
        IReadOnlyList<AccountItem> targets,
        CancellationToken externalCt)
    {
        if (_launcher == null || targets.Count == 0 || IsLaunching)
        {
            return (0, null);
        }

        string effectiveTargetInput;
        LaunchTarget parsedTarget;
        try
        {
            effectiveTargetInput = ResolveEffectiveTargetInput();
            parsedTarget = RobloxLauncher.ParseTargetInput(effectiveTargetInput, _isFollowUserTargetMode);
        }
        catch (Exception ex)
        {
            _setLocalizedStatus("Str_Accounts_StatusInvalidLaunchTarget", Array.Empty<object?>());
            AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogInvalidLaunchTargetFormat", ex.Message));
            return (0, new LaunchResult(false, string.Empty, new LaunchTarget(LaunchTargetKind.Standard, null), DateTimeOffset.UtcNow, null, ex.Message));
        }

        if (_settings.MultiInstanceEnabled && _multiInstanceController != null)
        {
            var gateResult = LaunchGate.VerifyLaunchReadiness(_multiInstanceController, true);
            if (!gateResult.CanLaunch)
            {
                string reason = gateResult.WarningMessage ?? LaunchGate.BuildDiagnosticBlockedMessage(gateResult.Status);
                _setLocalizedStatus("Str_Accounts_StatusBlockedNoMutex", Array.Empty<object?>());
                AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogBlockedNoMutexFormat", reason));
                RefreshMultiInstanceStatus();
                return (0, new LaunchResult(false, string.Empty, parsedTarget, DateTimeOffset.UtcNow, null, reason));
            }
        }

        _launchCts?.Dispose();
        _launchCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        OnPropertyChanged(nameof(CanCancelLaunch));
        var ct = _launchCts.Token;

        bool hasAnySettingsOverride = targets.Any(t => t.Account.RobloxSettings?.HasOverrides == true);
        string? settingsBackup = null;
        if (hasAnySettingsOverride)
        {
            try
            {
                settingsBackup = _globalSettingsManager.CaptureBackup();
                if (settingsBackup != null)
                {
                    AppendActivityLog(LocalizationService.Instance.GetString("Str_Accounts_LogSettingsBackedUp"));
                }
            }
            catch (Exception ex)
            {
                AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogSettingsBackupFailedFormat", ex.Message));
            }
        }

        IsLaunching = true;
        _setLocalizedStatus("Str_Accounts_StatusBatchLaunchingFormat", [targets.Count]);

        int successCount = 0;
        LaunchResult? lastResult = null;

        try
        {
            if (_launcher is BootstrapperOrchestrator orchestrator)
            {
                _setLocalizedStatus("Str_Accounts_StatusPreparingBatch", Array.Empty<object?>());
                await orchestrator.PrepareForBatchLaunchAsync(ct);
            }

            for (int i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var item = targets[i];
                _setLocalizedStatus("Str_Accounts_StatusLaunchingItemFormat", [item.Username, i + 1, targets.Count]);
                AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_StatusLaunchingItemFormat", item.Username, i + 1, targets.Count));

                if (settingsBackup != null && i > 0)
                {
                    try
                    {
                        _globalSettingsManager.RestoreBackup(settingsBackup);
                    }
                    catch (Exception ex)
                    {
                        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogSettingsRestoreFailedFormat", ex.Message));
                    }
                }

                if (item.Account.RobloxSettings?.HasOverrides == true)
                {
                    try
                    {
                        _globalSettingsManager.ApplyProfile(item.Account.RobloxSettings);
                        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogAppliedSettingsFormat", item.Username, item.SettingsSummary));
                    }
                    catch (Exception ex)
                    {
                        AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogApplySettingsFailedFormat", item.Username, ex.Message));
                    }
                }

                var previousPids = _processSupervisor?.CaptureRunningClientPids() ?? new HashSet<int>();
                bool launchSucceeded = false;

                try
                {
                    LaunchResult result = _launcher is BootstrapperOrchestrator orch
                        ? await orch.LaunchAccountAsync(item.Account, parsedTarget, skipDeploymentCheck: true, ct)
                        : await _launcher.LaunchAccountAsync(item.Account, parsedTarget, ct);

                    lastResult = result;
                    if (result.Success)
                    {
                        _accountStore?.Upsert(item.Account);
                        item.Refresh();
                        item.SetCookieHealth(AccountCookieHealth.Valid);
                        successCount++;
                        launchSucceeded = true;

                        AppendActivityLog(LocalizationService.Instance.Format(
                            "Str_Accounts_LogLaunchedItemFormat",
                            item.Username,
                            result.Target.PlaceId?.ToString() ?? (result.Target.UserId?.ToString() ?? "-"),
                            result.Target.Kind));
                    }
                    else
                    {
                        AppendActivityLog(LocalizationService.Instance.Format(
                            "Str_Accounts_LogLaunchItemFailedFormat",
                            item.Username,
                            result.ErrorMessage ?? LocalizationService.Instance.GetString("Str_Dialog_LaunchFailedFallback")));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (RobloxApiException ex)
                {
                    string detail = $"{ex.StatusCode}: {ex.Message}";
                    if (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                    {
                        item.SetCookieHealth(AccountCookieHealth.Invalid, detail);
                    }
                    lastResult = new LaunchResult(false, string.Empty, parsedTarget, DateTimeOffset.UtcNow, null, ex.Message);
                    AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogLaunchItemFailedFormat", item.Username, detail));
                }
                catch (Exception ex)
                {
                    lastResult = new LaunchResult(false, string.Empty, parsedTarget, DateTimeOffset.UtcNow, null, ex.Message);
                    AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogLaunchItemFailedFormat", item.Username, ex.Message));
                }

                bool shouldWaitForWindow = launchSucceeded && _processSupervisor != null && (i < targets.Count - 1 || settingsBackup != null);
                if (shouldWaitForWindow)
                {
                    int timeoutSeconds = Math.Max(1, (int)Math.Ceiling(WindowWaitTimeout.TotalSeconds));
                    _setLocalizedStatus("Str_Accounts_StatusWaitingWindowFormat", [item.Username, timeoutSeconds]);
                    AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogWaitingWindowFormat", item.Username));

                    var waitResult = await _processSupervisor!.WaitForNewClientMainWindowAsync(
                        previousPids,
                        WindowWaitTimeout,
                        ProcessSupervisor.DefaultWindowPollInterval,
                        ct);

                    if (waitResult.WindowReady)
                    {
                        AppendActivityLog(i < targets.Count - 1
                            ? LocalizationService.Instance.Format("Str_Accounts_LogWindowReadyNextFormat", item.Username, waitResult.ProcessId)
                            : LocalizationService.Instance.Format("Str_Accounts_LogWindowReadySingleFormat", item.Username, waitResult.ProcessId));
                    }
                    else if (waitResult.TimedOut)
                    {
                        AppendActivityLog(i < targets.Count - 1
                            ? LocalizationService.Instance.Format("Str_Accounts_LogWindowTimeoutNextFormat", item.Username, timeoutSeconds)
                            : LocalizationService.Instance.Format("Str_Accounts_LogWindowTimeoutSingleFormat", item.Username, timeoutSeconds));
                    }
                }
            }

            _setLocalizedStatus("Str_Accounts_StatusBatchCompletedFormat", [successCount, targets.Count]);
        }
        catch (OperationCanceledException)
        {
            _setLocalizedStatus("Str_Accounts_StatusBatchCancelledFormat", [successCount, targets.Count]);
            AppendActivityLog(LocalizationService.Instance.GetString("Str_Accounts_LogBatchCancelled"));
        }
        finally
        {
            if (settingsBackup != null)
            {
                try
                {
                    _globalSettingsManager.RestoreBackup(settingsBackup);
                    AppendActivityLog(LocalizationService.Instance.GetString("Str_Accounts_LogSettingsRestored"));
                }
                catch (Exception ex)
                {
                    AppendActivityLog(LocalizationService.Instance.Format("Str_Accounts_LogSettingsRestoreFailedFormat", ex.Message));
                }
            }

            _launchCts?.Dispose();
            _launchCts = null;
            IsLaunching = false;
            RefreshMultiInstanceStatus();
            RefreshProtocolStatus();
        }

        return (successCount, lastResult);
    }

    private string ResolveEffectiveTargetInput()
    {
        if (!string.IsNullOrWhiteSpace(_launchTargetInput))
        {
            return RobloxLauncher.BuildEffectiveTargetInput(_launchTargetInput, _launchJobId, _isFollowUserTargetMode);
        }

        if (!string.IsNullOrWhiteSpace(_launchShareLink))
        {
            string share = _launchShareLink.Trim();
            string normalizedShare = share.Contains("/share", StringComparison.OrdinalIgnoreCase) || share.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? share
                : $"https://www.roblox.com/share?code={Uri.EscapeDataString(share)}&type=Server";
            return RobloxLauncher.BuildEffectiveTargetInput(normalizedShare, null);
        }

        if (!string.IsNullOrWhiteSpace(_launchPlaceIdText))
        {
            return RobloxLauncher.BuildEffectiveTargetInput(_launchPlaceIdText, _launchJobId);
        }

        return RobloxLauncher.BuildEffectiveTargetInput(string.Empty, _launchJobId, _isFollowUserTargetMode);
    }

    public void CancelActiveLaunch()
    {
        if (_launchCts == null) return;

        _setLocalizedStatus("Str_Accounts_StatusCancelling", Array.Empty<object?>());
        try
        {
            _launchCts.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    public void ClearActivityLog()
    {
        _activityLogBuilder.Clear();
        OnPropertyChanged(nameof(ActivityLogText));
    }

    public void AppendActivityLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _activityLogBuilder.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        OnPropertyChanged(nameof(ActivityLogText));
        ActivityLogAppended?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatMultiInstanceStatusLog(string prefix, MultiInstanceStatus status)
    {
        string pidsText = status.RunningRobloxPids.Count > 0
            ? string.Join(", ", status.RunningRobloxPids)
            : "none";
        string errorPart = string.IsNullOrWhiteSpace(status.LastError)
            ? string.Empty
            : $", LastError={status.LastError}";
        return LocalizationService.Instance.Format(
            "Str_Accounts_LogEnabledFormat",
            prefix,
            status.OwnsMutex,
            status.WaitingForMutex,
            pidsText,
            errorPart);
    }
}
