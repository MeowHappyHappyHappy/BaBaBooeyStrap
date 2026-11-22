using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BaBaBooeyStrap.App.Interop;
using BaBaBooeyStrap.App.Views.Dialogs;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Integrations.Matchmaking;
using BaBaBooeyStrap.Integrations.Window;
using BaBaBooeyStrap.MultiInstance.Abstractions;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace BaBaBooeyStrap.App.Services;

public sealed class NotifyIconWrapper : IDisposable
{
    private const int MaxTrayTooltipLength = 63;

    private readonly bool _headlessMode;
    private readonly Action? _onOpenSettings;
    private readonly Action? _onLaunchRoblox;
    private readonly Func<MultiInstanceStatus>? _getMultiInstanceStatus;
    private readonly Action? _onToggleMultiInstance;
    private readonly Action? _onExitRequested;
    private readonly Func<IReadOnlyList<ActiveServerSessionInfo>>? _getActiveServers;
    private readonly Func<IReadOnlyList<RobloxAccount>>? _getAccounts;
    private readonly Func<IReadOnlyList<SavedJoinTarget>>? _getSavedTargets;
    private readonly Action<RobloxAccount, SavedJoinTarget?>? _onQuickLaunchAccount;
    private readonly Func<IReadOnlyList<RunningRobloxInstance>>? _getRunningInstances;
    private readonly Action<int>? _onFocusRunningInstance;
    private readonly Action? _onMinimizeAllInstances;
    private readonly Action<WindowTilingMode>? _onTileRunningInstances;
    private readonly Action? _onToggleFloatingBar;
    private readonly Func<bool>? _isFloatingBarVisible;
    private Dispatcher? _uiDispatcher;

    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _contextMenu;
    private WinForms.ToolStripMenuItem? _settingsMenuItem;
    private WinForms.ToolStripMenuItem? _launchMenuItem;
    private WinForms.ToolStripMenuItem? _quickLaunchMenuItem;
    private WinForms.ToolStripMenuItem? _runningWindowsMenuItem;
    private WinForms.ToolStripMenuItem? _tileWindowsMenuItem;
    private WinForms.ToolStripMenuItem? _floatingBarMenuItem;
    private WinForms.ToolStripMenuItem? _multiInstanceMenuItem;
    private WinForms.ToolStripMenuItem? _serverLocationMenuItem;
    private WinForms.ToolStripMenuItem? _exitMenuItem;
    private ActiveServerSessionInfo? _singleActiveServer;
    private Drawing.Icon? _ownedIcon;
    private BootstrapperIcon _iconKind = BootstrapperIcon.IconBloxstrap;
    private string? _customIconPath;
    private IReadOnlyList<RobloxAccount> _cachedAccounts = Array.Empty<RobloxAccount>();
    private IReadOnlyList<SavedJoinTarget> _cachedTargets = Array.Empty<SavedJoinTarget>();
    private int _quickLaunchCacheRefreshing;
    private bool _quickLaunchCacheWarmed;
    private HwndSource? _hwndSource;
    private uint _taskbarCreatedMessageId;
    private bool _disposed;

    public bool IsWindowVisible { get; private set; } = true;
    public bool IsTrayActive { get; private set; }
    public string? LastBalloonTitle { get; private set; }
    public string? LastBalloonMessage { get; private set; }
    public string? LastServerLocationText { get; private set; }
    public string? LastTooltipText { get; private set; }
    public IReadOnlyList<string> LastServerLocationSubItems { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> LastQuickLaunchAccountNames { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> LastRunningWindowLabels { get; private set; } = Array.Empty<string>();

    public IReadOnlyList<string> ContextMenuActions { get; } =
        [
            "Settings",
            "Launch Roblox",
            "Quick Launch",
            "Running Windows",
            "Tile Windows",
            "Toggle Floating Bar",
            "Multi-Instance Status",
            "Server Location",
            "Exit"
        ];

    public NotifyIconWrapper(
        bool headlessMode = false,
        Action? onOpenSettings = null,
        Action? onLaunchRoblox = null,
        Func<MultiInstanceStatus>? getMultiInstanceStatus = null,
        Action? onToggleMultiInstance = null,
        Action? onExitRequested = null,
        Func<IReadOnlyList<ActiveServerSessionInfo>>? getActiveServers = null,
        Func<IReadOnlyList<RobloxAccount>>? getAccounts = null,
        Func<IReadOnlyList<SavedJoinTarget>>? getSavedTargets = null,
        Action<RobloxAccount, SavedJoinTarget?>? onQuickLaunchAccount = null,
        Func<IReadOnlyList<RunningRobloxInstance>>? getRunningInstances = null,
        Action<int>? onFocusRunningInstance = null,
        Action? onMinimizeAllInstances = null,
        Action<WindowTilingMode>? onTileRunningInstances = null,
        Action? onToggleFloatingBar = null,
        Func<bool>? isFloatingBarVisible = null)
    {
        _headlessMode = headlessMode;
        _onOpenSettings = onOpenSettings;
        _onLaunchRoblox = onLaunchRoblox;
        _getMultiInstanceStatus = getMultiInstanceStatus;
        _onToggleMultiInstance = onToggleMultiInstance;
        _onExitRequested = onExitRequested;
        _getActiveServers = getActiveServers;
        _getAccounts = getAccounts;
        _getSavedTargets = getSavedTargets;
        _onQuickLaunchAccount = onQuickLaunchAccount;
        _getRunningInstances = getRunningInstances;
        _onFocusRunningInstance = onFocusRunningInstance;
        _onMinimizeAllInstances = onMinimizeAllInstances;
        _onTileRunningInstances = onTileRunningInstances;
        _onToggleFloatingBar = onToggleFloatingBar;
        _isFloatingBarVisible = isFloatingBarVisible;
        _uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread);

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    public void Initialize(BootstrapperIcon iconKind = BootstrapperIcon.IconBloxstrap, string? customIconPath = null)
    {
        if (_disposed) return;

        _iconKind = iconKind;
        _customIconPath = customIconPath;
        _uiDispatcher ??= Application.Current?.Dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread);
        IsTrayActive = true;
        if (_headlessMode)
        {
            RefreshServerLocationStatus();
            RefreshQuickLaunchMenu();
            RefreshRunningInstancesMenu();
            return;
        }

        CreateOrRecreateWinFormsIcon(iconKind, customIconPath);
        EnsureTaskbarCreatedHook();
        RefreshServerLocationStatus();
        RefreshRunningInstancesMenu();
        TriggerAsyncQuickLaunchCacheRefresh();
    }

    public void AttachWindow(Window window)
    {
        if (_headlessMode || window == null) return;

        var helper = new WindowInteropHelper(window);
        if (helper.Handle != nint.Zero)
        {
            AttachHwndSource(helper.Handle);
        }
        else
        {
            window.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(window).Handle;
                if (h != nint.Zero) AttachHwndSource(h);
            };
        }
    }

    public void MinimizeToTray()
    {
        IsWindowVisible = false;
        IsTrayActive = true;
    }

    public void RestoreWindow()
    {
        IsWindowVisible = true;
        InvokeOnUiDispatcher(_onOpenSettings);
    }

    private void InvokeOnUiDispatcher(Action? action)
    {
        if (action == null) return;

        var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;
        if (!_headlessMode && dispatcher != null)
        {
            try { dispatcher.BeginInvoke(action); } catch { }
        }
        else
        {
            action.Invoke();
        }
    }

    public void ShowBalloonNotification(string title, string message, int timeoutMs = 4000)
    {
        LastBalloonTitle = title;
        LastBalloonMessage = message;

        if (_headlessMode || _notifyIcon == null) return;

        void ShowCore()
        {
            if (_disposed || _notifyIcon == null) return;
            try
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = message;
                _notifyIcon.ShowBalloonTip(timeoutMs);
            }
            catch { }
        }

        try
        {
            var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(ShowCore));
            }
            else if (_contextMenu != null && _contextMenu.IsHandleCreated && _contextMenu.InvokeRequired)
            {
                _contextMenu.BeginInvoke(new Action(ShowCore));
            }
            else
            {
                ShowCore();
            }
        }
        catch { }
    }

    public static (string Title, string Message) FormatServerJoinNotification(ActiveServerSessionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        string title = !string.IsNullOrWhiteSpace(info.AccountUsername)
            ? LocalizationService.Instance.Format("Str_Tray_ServerJoinTitleAccountFormat", info.AccountUsername, info.ProcessId)
            : LocalizationService.Instance.Format("Str_Tray_ServerJoinTitlePidFormat", info.ProcessId);

        string endpoint = FormatEndpoint(info);
        string message = info.Location is ServerLocationInfo loc
            ? $"{loc.City}, {loc.Region}, {loc.Country} ({loc.RegionCode})\nIP: {endpoint}"
            : LocalizationService.Instance.Format("Str_Tray_ServerJoinIpOnlyFormat", endpoint);

        return (title, message);
    }

    public void ShowServerJoinToast(ActiveServerSessionInfo info, int durationMs = 5000)
    {
        ArgumentNullException.ThrowIfNull(info);

        var (title, message) = FormatServerJoinNotification(info);
        LastBalloonTitle = title;
        LastBalloonMessage = message;

        if (_headlessMode || _disposed) return;

        string endpointText = $"IP: {FormatEndpoint(info)}";
        string locationText = info.Location is ServerLocationInfo loc
            ? $"{loc.City}, {loc.Region}, {loc.Country} ({loc.RegionCode})"
            : LocalizationService.Instance.Format("Str_Tray_ServerJoinIpOnlyFormat", FormatEndpoint(info));
        string? country = info.Location?.Country;
        string iso = FlagImageService.ResolveIsoCode(country);

        _ = Task.Run(async () =>
        {
            byte[]? pngBytes = FlagImageService.TryGetCachedFlagPngBytes(country);
            if (pngBytes == null && !string.IsNullOrEmpty(iso))
            {
                try
                {
                    using var cts = new CancellationTokenSource(1500);
                    pngBytes = await FlagImageService.GetFlagPngBytesAsync(country, cts.Token).ConfigureAwait(false);
                }
                catch { }
            }

            if (_disposed) return;

            var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            _ = dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                var wpfFlag = FlagImageService.CreateWpfFlagImage(pngBytes);
                ServerJoinToastWindow.ShowToast(
                    title,
                    locationText,
                    endpointText,
                    iso,
                    wpfFlag,
                    durationMs,
                    onClick: () => CopyServerSessionDetails(info));
            });
        });
    }

    public void RefreshMultiInstanceMenuStatus()
    {
        if (_headlessMode || _multiInstanceMenuItem == null || _getMultiInstanceStatus == null)
            return;

        try
        {
            var status = _getMultiInstanceStatus();
            string mutexState = status.OwnsMutex
                ? LocalizationService.Instance.GetString("Str_Tray_StateActive")
                : (status.WaitingForMutex
                    ? LocalizationService.Instance.GetString("Str_Tray_StateBlocked")
                    : LocalizationService.Instance.GetString("Str_Tray_StateOff"));
            _multiInstanceMenuItem.Text = LocalizationService.Instance.Format(
                "Str_Tray_MultiInstanceMenuFormat",
                mutexState,
                status.RunningRobloxPids.Count);
            _multiInstanceMenuItem.Checked = status.Enabled;
        }
        catch { }
    }

    public void RefreshLocalizedMenuTexts()
    {
        if (_headlessMode)
        {
            RefreshServerLocationStatus();
            RefreshQuickLaunchMenu();
            RefreshRunningInstancesMenu();
            return;
        }

        if (_settingsMenuItem != null)
            _settingsMenuItem.Text = LocalizationService.Instance.GetString("Str_Tray_Settings");

        if (_launchMenuItem != null)
            _launchMenuItem.Text = LocalizationService.Instance.GetString("Str_Tray_LaunchRoblox");

        RefreshQuickLaunchMenu();
        RefreshRunningInstancesMenu();
        RefreshTileAndFloatingBarMenuItems();

        if (_multiInstanceMenuItem != null)
        {
            _multiInstanceMenuItem.Text = LocalizationService.Instance.GetString("Str_Tray_MultiInstanceStatus");
            RefreshMultiInstanceMenuStatus();
        }

        RefreshServerLocationStatusCore(forceMenuUpdate: true, forceTooltipUpdate: true);

        if (_exitMenuItem != null)
            _exitMenuItem.Text = LocalizationService.Instance.GetString("Str_Tray_Exit");
    }

    public void RefreshRunningInstancesMenu()
    {
        if (_disposed) return;

        IReadOnlyList<RunningRobloxInstance> instances;
        try
        {
            instances = _getRunningInstances?.Invoke() ?? Array.Empty<RunningRobloxInstance>();
        }
        catch
        {
            instances = Array.Empty<RunningRobloxInstance>();
        }

        LastRunningWindowLabels = instances
            .Select(i => $"#{i.SlotIndex} {i.EffectiveLabel} [{i.MonitorBadge}]")
            .ToList();

        if (_headlessMode) return;

        var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            try { dispatcher.BeginInvoke(new Action(RefreshRunningInstancesMenu)); } catch { }
            return;
        }

        if (_runningWindowsMenuItem == null) return;

        _runningWindowsMenuItem.Text = LocalizationService.Instance.Format(
            "Str_Tray_RunningWindowsFormat",
            instances.Count);
        ClearDropDownItems(_runningWindowsMenuItem);

        if (instances.Count == 0)
        {
            var emptyItem = new WinForms.ToolStripMenuItem(
                LocalizationService.Instance.GetString("Str_Tray_RunningWindowsEmpty"))
            {
                Enabled = false
            };
            _runningWindowsMenuItem.DropDownItems.Add(emptyItem);
            return;
        }

        foreach (var inst in instances)
        {
            int capturedPid = inst.ProcessId;
            string minSuffix = inst.IsMinimized
                ? $" ({LocalizationService.Instance.GetString("Str_QuickSwitcher_MinimizedState")})"
                : string.Empty;
            string label = $"#{inst.SlotIndex} {inst.EffectiveLabel} [{inst.MonitorBadge}]{minSuffix}";

            var winItem = new WinForms.ToolStripMenuItem(
                label,
                null,
                (_, _) => InvokeOnUiDispatcher(() => _onFocusRunningInstance?.Invoke(capturedPid)))
            {
                Checked = inst.IsFocused
            };
            _runningWindowsMenuItem.DropDownItems.Add(winItem);
        }

        _runningWindowsMenuItem.DropDownItems.Add(new WinForms.ToolStripSeparator());
        var minAllItem = new WinForms.ToolStripMenuItem(
            LocalizationService.Instance.GetString("Str_Tray_MinimizeAllWindows"),
            null,
            (_, _) => InvokeOnUiDispatcher(_onMinimizeAllInstances));
        _runningWindowsMenuItem.DropDownItems.Add(minAllItem);
    }

    private void RefreshTileAndFloatingBarMenuItems()
    {
        if (_headlessMode || _disposed) return;

        if (_tileWindowsMenuItem != null)
        {
            _tileWindowsMenuItem.Text = LocalizationService.Instance.GetString("Str_Tray_TileWindows");
            ClearDropDownItems(_tileWindowsMenuItem);

            var gridItem = new WinForms.ToolStripMenuItem(
                LocalizationService.Instance.GetString("Str_Tiling_SingleMonitorGrid"),
                null,
                (_, _) => InvokeOnUiDispatcher(() => _onTileRunningInstances?.Invoke(WindowTilingMode.SingleMonitorGrid)));
            var spreadItem = new WinForms.ToolStripMenuItem(
                LocalizationService.Instance.GetString("Str_Tiling_SpreadAcrossMonitors"),
                null,
                (_, _) => InvokeOnUiDispatcher(() => _onTileRunningInstances?.Invoke(WindowTilingMode.SpreadAcrossMonitors)));
            var mainSideItem = new WinForms.ToolStripMenuItem(
                LocalizationService.Instance.GetString("Str_Tiling_MainAndSide"),
                null,
                (_, _) => InvokeOnUiDispatcher(() => _onTileRunningInstances?.Invoke(WindowTilingMode.MainAndSide)));

            _tileWindowsMenuItem.DropDownItems.Add(gridItem);
            _tileWindowsMenuItem.DropDownItems.Add(spreadItem);
            _tileWindowsMenuItem.DropDownItems.Add(mainSideItem);
        }

        if (_floatingBarMenuItem != null)
        {
            bool visible = false;
            try { visible = _isFloatingBarVisible?.Invoke() == true; } catch { }
            _floatingBarMenuItem.Text = LocalizationService.Instance.GetString("Str_Tray_ToggleFloatingBar");
            _floatingBarMenuItem.Checked = visible;
        }
    }

    public void RefreshQuickLaunchMenu()
    {
        if (_disposed) return;

        if (_headlessMode)
        {
            IReadOnlyList<RobloxAccount> syncAccounts;
            IReadOnlyList<SavedJoinTarget> syncTargets;
            try { syncAccounts = _getAccounts?.Invoke() ?? Array.Empty<RobloxAccount>(); } catch { syncAccounts = Array.Empty<RobloxAccount>(); }
            try { syncTargets = _getSavedTargets?.Invoke() ?? Array.Empty<SavedJoinTarget>(); } catch { syncTargets = Array.Empty<SavedJoinTarget>(); }

            Volatile.Write(ref _cachedAccounts, syncAccounts);
            Volatile.Write(ref _cachedTargets, syncTargets);
            _quickLaunchCacheWarmed = true;
            LastQuickLaunchAccountNames = syncAccounts.Select(a => a.Username).ToList();
            return;
        }

        var accounts = Volatile.Read(ref _cachedAccounts);
        var targets = Volatile.Read(ref _cachedTargets);
        LastQuickLaunchAccountNames = accounts.Select(a => a.Username).ToList();

        RebuildQuickLaunchMenuItems(accounts, targets);
        TriggerAsyncQuickLaunchCacheRefresh();
    }

    private void TriggerAsyncQuickLaunchCacheRefresh()
    {
        if (_disposed || _headlessMode) return;
        if (Interlocked.CompareExchange(ref _quickLaunchCacheRefreshing, 1, 0) != 0) return;

        _ = Task.Run(() =>
        {
            try
            {
                if (_disposed) return;

                IReadOnlyList<RobloxAccount> loadedAccounts;
                IReadOnlyList<SavedJoinTarget> loadedTargets;
                try { loadedAccounts = _getAccounts?.Invoke() ?? Array.Empty<RobloxAccount>(); } catch { loadedAccounts = Array.Empty<RobloxAccount>(); }
                try { loadedTargets = _getSavedTargets?.Invoke() ?? Array.Empty<SavedJoinTarget>(); } catch { loadedTargets = Array.Empty<SavedJoinTarget>(); }

                var prevAccounts = Volatile.Read(ref _cachedAccounts);
                var prevTargets = Volatile.Read(ref _cachedTargets);
                bool changed = !_quickLaunchCacheWarmed ||
                               !AccountsSnapshotEquals(prevAccounts, loadedAccounts) ||
                               !TargetsSnapshotEquals(prevTargets, loadedTargets);

                Volatile.Write(ref _cachedAccounts, loadedAccounts);
                Volatile.Write(ref _cachedTargets, loadedTargets);
                _quickLaunchCacheWarmed = true;

                if (changed && !_disposed)
                {
                    InvokeOnUiDispatcher(() =>
                    {
                        if (_disposed) return;
                        var accSnap = Volatile.Read(ref _cachedAccounts);
                        var tgtSnap = Volatile.Read(ref _cachedTargets);
                        LastQuickLaunchAccountNames = accSnap.Select(a => a.Username).ToList();
                        RebuildQuickLaunchMenuItems(accSnap, tgtSnap);
                    });
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _quickLaunchCacheRefreshing, 0);
            }
        });
    }

    private static bool AccountsSnapshotEquals(IReadOnlyList<RobloxAccount> a, IReadOnlyList<RobloxAccount> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || !string.Equals(a[i].Username, b[i].Username, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool TargetsSnapshotEquals(IReadOnlyList<SavedJoinTarget> a, IReadOnlyList<SavedJoinTarget> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id ||
                !string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal) ||
                !string.Equals(a[i].GameName, b[i].GameName, StringComparison.Ordinal) ||
                !string.Equals(a[i].TargetInput, b[i].TargetInput, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private void RebuildQuickLaunchMenuItems(IReadOnlyList<RobloxAccount> accounts, IReadOnlyList<SavedJoinTarget> targets)
    {
        if (_headlessMode || _quickLaunchMenuItem == null || _disposed)
        {
            return;
        }

        _quickLaunchMenuItem.Text = LocalizationService.Instance.GetString("Str_Tray_QuickLaunch");
        ClearDropDownItems(_quickLaunchMenuItem);

        if (accounts.Count == 0)
        {
            var emptyItem = new WinForms.ToolStripMenuItem(
                LocalizationService.Instance.GetString("Str_Tray_QuickLaunchEmpty"))
            {
                Enabled = false
            };
            _quickLaunchMenuItem.DropDownItems.Add(emptyItem);
            return;
        }

        foreach (var account in accounts)
        {
            var capturedAccount = account;
            if (targets.Count == 0)
            {
                var accItem = new WinForms.ToolStripMenuItem(
                    capturedAccount.Username,
                    null,
                    (_, _) => InvokeOnUiDispatcher(() => _onQuickLaunchAccount?.Invoke(capturedAccount, null)));
                _quickLaunchMenuItem.DropDownItems.Add(accItem);
                continue;
            }

            var accParent = new WinForms.ToolStripMenuItem(capturedAccount.Username);
            var lastTargetItem = new WinForms.ToolStripMenuItem(
                LocalizationService.Instance.GetString("Str_Tray_QuickLaunchLastTarget"),
                null,
                (_, _) => InvokeOnUiDispatcher(() => _onQuickLaunchAccount?.Invoke(capturedAccount, null)));
            accParent.DropDownItems.Add(lastTargetItem);
            accParent.DropDownItems.Add(new WinForms.ToolStripSeparator());

            foreach (var target in targets)
            {
                var capturedTarget = target;
                string label = !string.IsNullOrWhiteSpace(capturedTarget.GameName) &&
                               !string.Equals(capturedTarget.GameName, capturedTarget.Name, StringComparison.OrdinalIgnoreCase)
                    ? $"{capturedTarget.Name} ({capturedTarget.GameName})"
                    : capturedTarget.Name;
                var targetItem = new WinForms.ToolStripMenuItem(
                    label,
                    null,
                    (_, _) => InvokeOnUiDispatcher(() => _onQuickLaunchAccount?.Invoke(capturedAccount, capturedTarget)));
                accParent.DropDownItems.Add(targetItem);
            }

            _quickLaunchMenuItem.DropDownItems.Add(accParent);
        }
    }

    private static void ClearDropDownItems(WinForms.ToolStripMenuItem parent)
    {
        while (parent.DropDownItems.Count > 0)
        {
            var item = parent.DropDownItems[0];
            parent.DropDownItems.RemoveAt(0);
            if (item is WinForms.ToolStripMenuItem menuItem)
            {
                ClearDropDownItems(menuItem);
            }
            try { item.Dispose(); } catch { }
        }
    }

    public void RefreshServerLocationStatus()
    {
        RefreshServerLocationStatusCore(forceMenuUpdate: false);
    }

    private void RefreshServerLocationStatusCore(bool forceMenuUpdate, bool forceTooltipUpdate = false)
    {
        if (_disposed) return;

        if (!_headlessMode)
        {
            var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                try { dispatcher.BeginInvoke(new Action(() => RefreshServerLocationStatusCore(forceMenuUpdate, forceTooltipUpdate))); } catch { }
                return;
            }

            if (_contextMenu != null && _contextMenu.IsHandleCreated && _contextMenu.InvokeRequired)
            {
                try { _contextMenu.BeginInvoke(new Action(() => RefreshServerLocationStatusCore(forceMenuUpdate, forceTooltipUpdate))); } catch { }
                return;
            }
        }

        IReadOnlyList<ActiveServerSessionInfo> servers;
        try
        {
            servers = _getActiveServers?.Invoke() ?? Array.Empty<ActiveServerSessionInfo>();
        }
        catch
        {
            servers = Array.Empty<ActiveServerSessionInfo>();
        }

        bool canMutateMenu = !_headlessMode &&
                             _serverLocationMenuItem != null &&
                             forceMenuUpdate;

        if (servers.Count == 0)
        {
            _singleActiveServer = null;
            string menuText = LocalizationService.Instance.GetString("Str_Tray_ServerLocationNone");
            string tooltipText = "BaBaBooeyStrap";
            LastServerLocationText = menuText;
            LastTooltipText = tooltipText;
            LastServerLocationSubItems = Array.Empty<string>();

            if (!_headlessMode)
            {
                if (canMutateMenu && _serverLocationMenuItem != null)
                {
                    ClearServerSubItems();
                    ClearMenuItemImage(_serverLocationMenuItem);
                    _serverLocationMenuItem.Text = menuText;
                }
                if (_notifyIcon != null && (!forceMenuUpdate || forceTooltipUpdate))
                {
                    try { _notifyIcon.Text = tooltipText; } catch { }
                }
            }
            return;
        }

        if (servers.Count == 1)
        {
            var server = servers[0];
            _singleActiveServer = server;
            string locSummary = FormatServerLocationSummary(server, includeRegionDetail: true);
            string shortLoc = FormatServerLocationSummary(server, includeRegionDetail: false);
            string endpoint = FormatEndpoint(server);
            string who = !string.IsNullOrWhiteSpace(server.AccountUsername)
                ? $"[{server.AccountUsername}] "
                : string.Empty;

            string menuText = LocalizationService.Instance.Format(
                "Str_Tray_ServerLocationSingleFormat",
                $"{who}{locSummary} [{endpoint}]");
            string tooltipText = TruncateTrayTooltip($"BaBaBooeyStrap — {shortLoc}");

            LastServerLocationText = menuText;
            LastTooltipText = tooltipText;
            LastServerLocationSubItems = Array.Empty<string>();

            if (!_headlessMode)
            {
                if (canMutateMenu && _serverLocationMenuItem != null)
                {
                    ClearServerSubItems();
                    SetMenuItemFlagImage(_serverLocationMenuItem, server.Location?.Country);
                    _serverLocationMenuItem.Text = menuText;
                }
                if (_notifyIcon != null && (!forceMenuUpdate || forceTooltipUpdate))
                {
                    try { _notifyIcon.Text = tooltipText; } catch { }
                }
            }
            return;
        }

        _singleActiveServer = null;
        string multiMenuText = LocalizationService.Instance.Format("Str_Tray_ServerLocationMultiFormat", servers.Count);
        var subLabels = new List<string>(servers.Count);
        foreach (var s in servers)
        {
            subLabels.Add(FormatMultiSessionMenuItemText(s));
        }

        var latest = servers[^1];
        string latestShort = FormatServerLocationSummary(latest, includeRegionDetail: false);
        string multiTooltip = TruncateTrayTooltip(
            LocalizationService.Instance.Format("Str_Tray_ServerTooltipMultiFormat", servers.Count, latestShort));

        LastServerLocationText = multiMenuText;
        LastTooltipText = multiTooltip;
        LastServerLocationSubItems = subLabels;

        if (!_headlessMode)
        {
            if (canMutateMenu && _serverLocationMenuItem != null)
            {
                ClearServerSubItems();
                ClearMenuItemImage(_serverLocationMenuItem);
                for (int i = 0; i < servers.Count; i++)
                {
                    var captured = servers[i];
                    var subItem = new WinForms.ToolStripMenuItem(
                        subLabels[i],
                        null,
                        (_, _) => CopyServerSessionDetails(captured));
                    SetMenuItemFlagImage(subItem, captured.Location?.Country);
                    _serverLocationMenuItem.DropDownItems.Add(subItem);
                }
                _serverLocationMenuItem.Text = multiMenuText;
            }
            if (_notifyIcon != null && (!forceMenuUpdate || forceTooltipUpdate))
            {
                try { _notifyIcon.Text = multiTooltip; } catch { }
            }
        }
    }

    private static void ClearMenuItemImage(WinForms.ToolStripMenuItem item)
    {
        if (item.Image != null)
        {
            var old = item.Image;
            item.Image = null;
            try { old.Dispose(); } catch { }
        }
    }

    private static void SetMenuItemFlagImage(WinForms.ToolStripMenuItem item, string? country)
    {
        ClearMenuItemImage(item);
        if (string.IsNullOrWhiteSpace(country)) return;

        byte[]? cachedBytes = FlagImageService.TryGetCachedFlagPngBytes(country);
        item.Image = FlagImageService.CreateMenuFlagBitmap(country, cachedBytes);

        if (cachedBytes == null && !string.IsNullOrEmpty(FlagImageService.ResolveIsoCode(country)))
        {
            _ = FlagImageService.GetFlagPngBytesAsync(country);
        }
    }

    private void ClearServerSubItems()
    {
        if (_serverLocationMenuItem == null || !_serverLocationMenuItem.HasDropDownItems)
            return;

        while (_serverLocationMenuItem.DropDownItems.Count > 0)
        {
            var oldItem = _serverLocationMenuItem.DropDownItems[0];
            _serverLocationMenuItem.DropDownItems.RemoveAt(0);
            if (oldItem.Image != null)
            {
                var oldImg = oldItem.Image;
                oldItem.Image = null;
                try { oldImg.Dispose(); } catch { }
            }
            try { oldItem.Dispose(); } catch { }
        }
    }

    internal static string TruncateTrayTooltip(string text, int maxLength = MaxTrayTooltipLength)
    {
        if (string.IsNullOrEmpty(text)) return "BaBaBooeyStrap";
        if (text.Length <= maxLength) return text;
        if (maxLength <= 3) return text[..maxLength];
        return text[..(maxLength - 3)] + "...";
    }

    internal static string FormatServerLocationSummary(ActiveServerSessionInfo session, bool includeRegionDetail)
    {
        if (session.Location is ServerLocationInfo loc)
        {
            if (includeRegionDetail)
            {
                return $"{loc.City}, {loc.Region}, {loc.Country} ({loc.RegionCode})";
            }

            string iso = FlagImageService.ResolveIsoCode(loc.Country);
            return string.IsNullOrEmpty(iso)
                ? $"{loc.City}, {loc.Country} ({loc.RegionCode})"
                : $"[{iso}] {loc.City}, {loc.Country} ({loc.RegionCode})";
        }

        return includeRegionDetail
            ? LocalizationService.Instance.GetString("Str_Tray_ServerLocationResolving")
            : session.ServerIp;
    }

    internal static string FormatMultiSessionMenuItemText(ActiveServerSessionInfo session)
    {
        string who = !string.IsNullOrWhiteSpace(session.AccountUsername)
            ? session.AccountUsername
            : $"PID {session.ProcessId}";
        string locText = FormatServerLocationSummary(session, includeRegionDetail: true);
        string endpoint = FormatEndpoint(session);
        return $"[{who}] {locText} — {endpoint}";
    }

    internal static string FormatClipboardSummary(ActiveServerSessionInfo session)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(session.AccountUsername))
        {
            sb.Append(session.AccountUsername).Append(" | ");
        }
        sb.Append(FormatServerLocationSummary(session, includeRegionDetail: true));
        sb.Append(" | IP: ").Append(FormatEndpoint(session));
        if (!string.IsNullOrWhiteSpace(session.PlaceId))
        {
            sb.Append(" | PlaceId: ").Append(session.PlaceId);
        }
        if (!string.IsNullOrWhiteSpace(session.JobId))
        {
            sb.Append(" | JobId: ").Append(session.JobId);
        }
        return sb.ToString();
    }

    private static string FormatEndpoint(ActiveServerSessionInfo session) =>
        session.ServerPort > 0
            ? $"{session.ServerIp}:{session.ServerPort}"
            : session.ServerIp;

    private void CopyServerSessionDetails(ActiveServerSessionInfo session)
    {
        string summary = FormatClipboardSummary(session);
        try
        {
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(() => Clipboard.SetText(summary));
            }
            else
            {
                Clipboard.SetText(summary);
            }
        }
        catch { }

        ShowBalloonNotification(
            LocalizationService.Instance.GetString("Str_Tray_ServerCopiedTitle"),
            LocalizationService.Instance.Format("Str_Tray_ServerCopiedMessageFormat", FormatEndpoint(session)),
            3000);
    }

    private void OnLanguageChanged(object? sender, string _)
    {
        if (_disposed || _headlessMode || _contextMenu == null) return;

        try
        {
            var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(RefreshLocalizedMenuTexts));
            }
            else if (_contextMenu.IsHandleCreated && _contextMenu.InvokeRequired)
            {
                _contextMenu.BeginInvoke(new Action(RefreshLocalizedMenuTexts));
            }
            else
            {
                RefreshLocalizedMenuTexts();
            }
        }
        catch { }
    }

    public void RecreateOnTaskbarCreated()
    {
        if (!IsTrayActive || _disposed) return;
        if (_headlessMode) return;

        if (_notifyIcon != null)
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Visible = true;
            }
            catch
            {
                CreateOrRecreateWinFormsIcon(_iconKind, _customIconPath);
            }
        }
        else
        {
            CreateOrRecreateWinFormsIcon(_iconKind, _customIconPath);
        }

        if (_notifyIcon != null)
        {
            try
            {
                _notifyIcon.Text = TruncateTrayTooltip(LastTooltipText ?? "BaBaBooeyStrap");
            }
            catch { }
        }
        RefreshLocalizedMenuTexts();
    }

    private void CreateOrRecreateWinFormsIcon(BootstrapperIcon iconKind, string? customIconPath)
    {
        DisposeWinFormsObjects();

        _iconKind = iconKind;
        _customIconPath = customIconPath;
        _ownedIcon = IconResolver.ResolveDrawingIcon(iconKind, customIconPath);

        _contextMenu = new WinForms.ContextMenuStrip();
        _contextMenu.Opening += (_, _) => RefreshLocalizedMenuTexts();

        _settingsMenuItem = new WinForms.ToolStripMenuItem(
            LocalizationService.Instance.GetString("Str_Tray_Settings"),
            null,
            (_, _) => RestoreWindow());
        _launchMenuItem = new WinForms.ToolStripMenuItem(
            LocalizationService.Instance.GetString("Str_Tray_LaunchRoblox"),
            null,
            (_, _) => InvokeOnUiDispatcher(_onLaunchRoblox));

        _quickLaunchMenuItem = new WinForms.ToolStripMenuItem(
            LocalizationService.Instance.GetString("Str_Tray_QuickLaunch"));

        _runningWindowsMenuItem = new WinForms.ToolStripMenuItem(
            LocalizationService.Instance.Format("Str_Tray_RunningWindowsFormat", 0));

        _tileWindowsMenuItem = new WinForms.ToolStripMenuItem(
            LocalizationService.Instance.GetString("Str_Tray_TileWindows"));

        _floatingBarMenuItem = new WinForms.ToolStripMenuItem(
            LocalizationService.Instance.GetString("Str_Tray_ToggleFloatingBar"),
            null,
            (_, _) => InvokeOnUiDispatcher(_onToggleFloatingBar));

        _multiInstanceMenuItem = new WinForms.ToolStripMenuItem(
            LocalizationService.Instance.GetString("Str_Tray_MultiInstanceStatus"),
            null,
            (_, _) => InvokeOnUiDispatcher(() =>
            {
                _onToggleMultiInstance?.Invoke();
                RefreshMultiInstanceMenuStatus();
            }));

        _serverLocationMenuItem = new WinForms.ToolStripMenuItem(
            LocalizationService.Instance.GetString("Str_Tray_ServerLocationNone"),
            null,
            (_, _) =>
            {
                if (_singleActiveServer != null && _serverLocationMenuItem?.DropDownItems.Count == 0)
                {
                    CopyServerSessionDetails(_singleActiveServer);
                }
            });

        _exitMenuItem = new WinForms.ToolStripMenuItem(
            LocalizationService.Instance.GetString("Str_Tray_Exit"),
            null,
            (_, _) => InvokeOnUiDispatcher(_onExitRequested));

        _contextMenu.Items.Add(_settingsMenuItem);
        _contextMenu.Items.Add(_launchMenuItem);
        _contextMenu.Items.Add(_quickLaunchMenuItem);
        _contextMenu.Items.Add(_runningWindowsMenuItem);
        _contextMenu.Items.Add(_tileWindowsMenuItem);
        _contextMenu.Items.Add(_floatingBarMenuItem);
        _contextMenu.Items.Add(new WinForms.ToolStripSeparator());
        _contextMenu.Items.Add(_multiInstanceMenuItem);
        _contextMenu.Items.Add(_serverLocationMenuItem);
        _contextMenu.Items.Add(new WinForms.ToolStripSeparator());
        _contextMenu.Items.Add(_exitMenuItem);

        _notifyIcon = new WinForms.NotifyIcon(new Container())
        {
            Icon = _ownedIcon,
            Text = "BaBaBooeyStrap",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => RestoreWindow();
    }

    private void EnsureTaskbarCreatedHook()
    {
        if (_taskbarCreatedMessageId == 0)
        {
            _taskbarCreatedMessageId = AppNativeMethods.RegisterWindowMessageW("TaskbarCreated");
        }
    }

    private void AttachHwndSource(nint hwnd)
    {
        if (_hwndSource != null) return;
        EnsureTaskbarCreatedHook();
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_taskbarCreatedMessageId != 0 && (uint)msg == _taskbarCreatedMessageId)
        {
            RecreateOnTaskbarCreated();
        }
        return nint.Zero;
    }

    private void DisposeWinFormsObjects()
    {
        if (_notifyIcon != null)
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch { }
            _notifyIcon = null;
        }

        if (_serverLocationMenuItem != null)
        {
            ClearServerSubItems();
            ClearMenuItemImage(_serverLocationMenuItem);
        }

        if (_quickLaunchMenuItem != null)
        {
            ClearDropDownItems(_quickLaunchMenuItem);
        }

        if (_runningWindowsMenuItem != null)
        {
            ClearDropDownItems(_runningWindowsMenuItem);
        }

        if (_tileWindowsMenuItem != null)
        {
            ClearDropDownItems(_tileWindowsMenuItem);
        }

        _settingsMenuItem = null;
        _launchMenuItem = null;
        _quickLaunchMenuItem = null;
        _runningWindowsMenuItem = null;
        _tileWindowsMenuItem = null;
        _floatingBarMenuItem = null;
        _multiInstanceMenuItem = null;
        _serverLocationMenuItem = null;
        _singleActiveServer = null;
        _exitMenuItem = null;

        if (_contextMenu != null)
        {
            try { _contextMenu.Dispose(); } catch { }
            _contextMenu = null;
        }

        if (_ownedIcon != null)
        {
            try { _ownedIcon.Dispose(); } catch { }
            _ownedIcon = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsTrayActive = false;

        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

        if (_hwndSource != null)
        {
            try { _hwndSource.RemoveHook(WndProc); } catch { }
            _hwndSource = null;
        }

        DisposeWinFormsObjects();
        GC.SuppressFinalize(this);
    }
}
