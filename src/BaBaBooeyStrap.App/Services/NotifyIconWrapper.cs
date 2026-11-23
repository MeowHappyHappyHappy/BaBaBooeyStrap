using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BaBaBooeyStrap.App.Interop;
using BaBaBooeyStrap.App.Views.Dialogs;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Integrations.Matchmaking;
using BaBaBooeyStrap.Integrations.Window;
using BaBaBooeyStrap.MultiInstance.Abstractions;
using Wpf.Ui.Controls;
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
    private Container? _components;
    private TrayContextMenuWindow? _trayMenuWindow;
    private Drawing.Icon? _ownedIcon;
    private BootstrapperIcon _iconKind = BootstrapperIcon.IconBloxstrap;
    private string? _customIconPath;
    private IReadOnlyList<RobloxAccount> _cachedAccounts = Array.Empty<RobloxAccount>();
    private IReadOnlyList<SavedJoinTarget> _cachedTargets = Array.Empty<SavedJoinTarget>();
    private IReadOnlyList<ActiveServerSessionInfo> _latestActiveServers = Array.Empty<ActiveServerSessionInfo>();
    private int _quickLaunchCacheRefreshing;
    private bool _quickLaunchCacheWarmed;
    private HwndSource? _hwndSource;
    private bool _ownsHwndSource;
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
        EnsureMessageOnlyHwndSource();
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
        // Multi-instance status is now read dynamically when opening the WPF Fluent tray menu.
    }

    public void RefreshLocalizedMenuTexts()
    {
        RefreshServerLocationStatusCore(forceTooltipUpdate: true);
        RefreshQuickLaunchMenu();
        RefreshRunningInstancesMenu();
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
        LastQuickLaunchAccountNames = accounts.Select(a => a.Username).ToList();
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

                Volatile.Write(ref _cachedAccounts, loadedAccounts);
                Volatile.Write(ref _cachedTargets, loadedTargets);
                _quickLaunchCacheWarmed = true;
                LastQuickLaunchAccountNames = loadedAccounts.Select(a => a.Username).ToList();
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _quickLaunchCacheRefreshing, 0);
            }
        });
    }

    public void RefreshServerLocationStatus()
    {
        RefreshServerLocationStatusCore();
    }

    private void RefreshServerLocationStatusCore(bool forceTooltipUpdate = false)
    {
        if (_disposed) return;

        if (!_headlessMode)
        {
            var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                try { dispatcher.BeginInvoke(new Action(() => RefreshServerLocationStatusCore(forceTooltipUpdate))); } catch { }
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

        _latestActiveServers = servers;

        if (servers.Count == 0)
        {
            string menuText = LocalizationService.Instance.GetString("Str_Tray_ServerLocationNone");
            string tooltipText = "BaBaBooeyStrap";
            LastServerLocationText = menuText;
            LastTooltipText = tooltipText;
            LastServerLocationSubItems = Array.Empty<string>();

            if (!_headlessMode && _notifyIcon != null)
            {
                try { _notifyIcon.Text = tooltipText; } catch { }
            }
            return;
        }

        if (servers.Count == 1)
        {
            var server = servers[0];
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

            PrewarmFlagCache(server.Location?.Country);

            if (!_headlessMode && _notifyIcon != null)
            {
                try { _notifyIcon.Text = tooltipText; } catch { }
            }
            return;
        }

        string multiMenuText = LocalizationService.Instance.Format("Str_Tray_ServerLocationMultiFormat", servers.Count);
        var subLabels = new List<string>(servers.Count);
        foreach (var s in servers)
        {
            subLabels.Add(FormatMultiSessionMenuItemText(s));
            PrewarmFlagCache(s.Location?.Country);
        }

        var latest = servers[^1];
        string latestShort = FormatServerLocationSummary(latest, includeRegionDetail: false);
        string multiTooltip = TruncateTrayTooltip(
            LocalizationService.Instance.Format("Str_Tray_ServerTooltipMultiFormat", servers.Count, latestShort));

        LastServerLocationText = multiMenuText;
        LastTooltipText = multiTooltip;
        LastServerLocationSubItems = subLabels;

        if (!_headlessMode && _notifyIcon != null)
        {
            try { _notifyIcon.Text = multiTooltip; } catch { }
        }
    }

    private static void PrewarmFlagCache(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return;
        byte[]? cachedBytes = FlagImageService.TryGetCachedFlagPngBytes(country);
        if (cachedBytes == null && !string.IsNullOrEmpty(FlagImageService.ResolveIsoCode(country)))
        {
            _ = FlagImageService.GetFlagPngBytesAsync(country);
        }
    }

    private static ImageSource? ResolveFlagImageSource(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return null;
        byte[]? cachedBytes = FlagImageService.TryGetCachedFlagPngBytes(country);
        if (cachedBytes != null)
        {
            return FlagImageService.CreateWpfFlagImage(cachedBytes);
        }

        PrewarmFlagCache(country);
        return null;
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
        if (_disposed || _headlessMode) return;

        try
        {
            var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(RefreshLocalizedMenuTexts));
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
        _components = new Container();

        _notifyIcon = new WinForms.NotifyIcon(_components)
        {
            Icon = _ownedIcon,
            Text = "BaBaBooeyStrap",
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => RestoreWindow();
        _notifyIcon.MouseUp += OnNotifyIconMouseUp;
    }

    private void OnNotifyIconMouseUp(object? sender, WinForms.MouseEventArgs e)
    {
        if (_disposed || _headlessMode) return;
        if (e.Button == WinForms.MouseButtons.Right)
        {
            InvokeOnUiDispatcher(ShowWpfTrayContextMenu);
        }
    }

    private void ShowWpfTrayContextMenu()
    {
        if (_disposed || _headlessMode) return;

        RefreshServerLocationStatusCore(forceTooltipUpdate: false);
        RefreshRunningInstancesMenu();
        TriggerAsyncQuickLaunchCacheRefresh();

        if (!User32.GetCursorPos(out var cursorPt))
        {
            cursorPt = new User32.POINT { X = 100, Y = 100 };
        }

        User32.RECT workArea = ResolveMonitorWorkAreaFromPoint(cursorPt);
        TrayMenuSnapshot snapshot = BuildTrayMenuSnapshot();

        _trayMenuWindow ??= new TrayContextMenuWindow();
        _trayMenuWindow.ShowAtCursor(snapshot, cursorPt.X, cursorPt.Y, workArea);
    }

    private TrayMenuSnapshot BuildTrayMenuSnapshot()
    {
        if (!_quickLaunchCacheWarmed)
        {
            try
            {
                var syncAccounts = _getAccounts?.Invoke() ?? Array.Empty<RobloxAccount>();
                var syncTargets = _getSavedTargets?.Invoke() ?? Array.Empty<SavedJoinTarget>();
                Volatile.Write(ref _cachedAccounts, syncAccounts);
                Volatile.Write(ref _cachedTargets, syncTargets);
                _quickLaunchCacheWarmed = true;
                LastQuickLaunchAccountNames = syncAccounts.Select(a => a.Username).ToList();
            }
            catch { }
        }

        IReadOnlyList<RunningRobloxInstance> instances;
        try
        {
            instances = _getRunningInstances?.Invoke() ?? Array.Empty<RunningRobloxInstance>();
        }
        catch
        {
            instances = Array.Empty<RunningRobloxInstance>();
        }

        MultiInstanceStatus? miStatus = null;
        try
        {
            miStatus = _getMultiInstanceStatus?.Invoke();
        }
        catch { }

        TrayMultiInstanceState badgeState = miStatus == null
            ? TrayMultiInstanceState.Off
            : (miStatus.OwnsMutex
                ? TrayMultiInstanceState.Active
                : (miStatus.WaitingForMutex ? TrayMultiInstanceState.Blocked : TrayMultiInstanceState.Off));

        string stateLabel = badgeState switch
        {
            TrayMultiInstanceState.Active => LocalizationService.Instance.GetString("Str_Tray_StateActive"),
            TrayMultiInstanceState.Blocked => LocalizationService.Instance.GetString("Str_Tray_StateBlocked"),
            _ => LocalizationService.Instance.GetString("Str_Tray_StateOff")
        };

        int runningCount = miStatus?.RunningRobloxPids.Count ?? instances.Count;
        string headerBadgeText = $"{stateLabel} · {runningCount}";

        var items = new List<TrayMenuItemModel>
        {
            new(
                Text: LocalizationService.Instance.GetString("Str_Tray_Settings"),
                IconSymbol: SymbolRegular.Settings24,
                OnClick: RestoreWindow),
            new(
                Text: LocalizationService.Instance.GetString("Str_Tray_LaunchRoblox"),
                IconSymbol: SymbolRegular.Play24,
                OnClick: () => InvokeOnUiDispatcher(_onLaunchRoblox)),
            new(
                Text: LocalizationService.Instance.GetString("Str_Tray_QuickLaunch"),
                IconSymbol: SymbolRegular.Rocket24,
                Children: BuildQuickLaunchMenuChildren()),
            new(
                Text: LocalizationService.Instance.Format("Str_Tray_RunningWindowsFormat", instances.Count),
                IconSymbol: SymbolRegular.WindowMultiple20,
                BadgeText: instances.Count > 0 ? instances.Count.ToString() : null,
                Children: BuildRunningWindowsMenuChildren(instances)),
            new(
                Text: LocalizationService.Instance.GetString("Str_Tray_TileWindows"),
                IconSymbol: SymbolRegular.Grid24,
                Children: BuildTileWindowsMenuChildren()),
            new(
                Text: LocalizationService.Instance.GetString("Str_Tray_ToggleFloatingBar"),
                IconSymbol: SymbolRegular.PanelBottom20,
                IsChecked: SafeIsFloatingBarVisible(),
                OnClick: () => InvokeOnUiDispatcher(_onToggleFloatingBar)),
            TrayMenuItemModel.Separator()
        };

        string multiInstanceLabel = miStatus != null
            ? LocalizationService.Instance.Format("Str_Tray_MultiInstanceMenuFormat", stateLabel, miStatus.RunningRobloxPids.Count)
            : LocalizationService.Instance.GetString("Str_Tray_MultiInstanceStatus");

        items.Add(new TrayMenuItemModel(
            Text: multiInstanceLabel,
            IconSymbol: SymbolRegular.ShieldCheckmark24,
            IsChecked: miStatus?.Enabled == true,
            OnClick: () => InvokeOnUiDispatcher(() =>
            {
                _onToggleMultiInstance?.Invoke();
                RefreshMultiInstanceMenuStatus();
            })));

        items.Add(BuildServerLocationMenuItem());
        items.Add(TrayMenuItemModel.Separator());
        items.Add(new TrayMenuItemModel(
            Text: LocalizationService.Instance.GetString("Str_Tray_Exit"),
            IconSymbol: SymbolRegular.Power24,
            IsDanger: true,
            OnClick: () => InvokeOnUiDispatcher(_onExitRequested)));

        return new TrayMenuSnapshot(
            AppTitle: "BaBaBooeyStrap",
            VersionText: ResolveAppVersionLabel(),
            MultiInstanceBadgeText: headerBadgeText,
            MultiInstanceBadgeState: badgeState,
            RunningCount: runningCount,
            Items: items);
    }

    private IReadOnlyList<TrayMenuItemModel> BuildQuickLaunchMenuChildren()
    {
        var accounts = Volatile.Read(ref _cachedAccounts);
        var targets = Volatile.Read(ref _cachedTargets);

        if (accounts.Count == 0)
        {
            return
            [
                new TrayMenuItemModel(
                    Text: LocalizationService.Instance.GetString("Str_Tray_QuickLaunchEmpty"),
                    IsEnabled: false)
            ];
        }

        var result = new List<TrayMenuItemModel>(accounts.Count);
        foreach (var account in accounts)
        {
            var capturedAccount = account;
            if (targets.Count == 0)
            {
                result.Add(new TrayMenuItemModel(
                    Text: capturedAccount.Username,
                    IconSymbol: SymbolRegular.Person24,
                    OnClick: () => InvokeOnUiDispatcher(() => _onQuickLaunchAccount?.Invoke(capturedAccount, null))));
                continue;
            }

            var targetChildren = new List<TrayMenuItemModel>(targets.Count + 2)
            {
                new(
                    Text: LocalizationService.Instance.GetString("Str_Tray_QuickLaunchLastTarget"),
                    IconSymbol: SymbolRegular.ArrowClockwise24,
                    OnClick: () => InvokeOnUiDispatcher(() => _onQuickLaunchAccount?.Invoke(capturedAccount, null))),
                TrayMenuItemModel.Separator()
            };

            foreach (var target in targets)
            {
                var capturedTarget = target;
                bool hasDistinctGame = !string.IsNullOrWhiteSpace(capturedTarget.GameName) &&
                                       !string.Equals(capturedTarget.GameName, capturedTarget.Name, StringComparison.OrdinalIgnoreCase);
                targetChildren.Add(new TrayMenuItemModel(
                    Text: capturedTarget.Name,
                    Subtitle: hasDistinctGame ? capturedTarget.GameName : null,
                    IconSymbol: SymbolRegular.Target24,
                    OnClick: () => InvokeOnUiDispatcher(() => _onQuickLaunchAccount?.Invoke(capturedAccount, capturedTarget))));
            }

            result.Add(new TrayMenuItemModel(
                Text: capturedAccount.Username,
                IconSymbol: SymbolRegular.Person24,
                Children: targetChildren));
        }

        return result;
    }

    private IReadOnlyList<TrayMenuItemModel> BuildRunningWindowsMenuChildren(IReadOnlyList<RunningRobloxInstance> instances)
    {
        if (instances.Count == 0)
        {
            return
            [
                new TrayMenuItemModel(
                    Text: LocalizationService.Instance.GetString("Str_Tray_RunningWindowsEmpty"),
                    IsEnabled: false)
            ];
        }

        var children = new List<TrayMenuItemModel>(instances.Count + 2);
        foreach (var inst in instances)
        {
            int capturedPid = inst.ProcessId;
            string? subtitle = inst.IsMinimized
                ? LocalizationService.Instance.GetString("Str_QuickSwitcher_MinimizedState")
                : (!string.IsNullOrWhiteSpace(inst.Notes) ? inst.Notes : null);

            children.Add(new TrayMenuItemModel(
                Text: $"#{inst.SlotIndex} {inst.EffectiveLabel}",
                Subtitle: subtitle,
                BadgeText: inst.MonitorBadge,
                IsChecked: inst.IsFocused,
                OnClick: () => InvokeOnUiDispatcher(() => _onFocusRunningInstance?.Invoke(capturedPid))));
        }

        children.Add(TrayMenuItemModel.Separator());
        children.Add(new TrayMenuItemModel(
            Text: LocalizationService.Instance.GetString("Str_Tray_MinimizeAllWindows"),
            IconSymbol: SymbolRegular.Subtract24,
            OnClick: () => InvokeOnUiDispatcher(_onMinimizeAllInstances)));

        return children;
    }

    private IReadOnlyList<TrayMenuItemModel> BuildTileWindowsMenuChildren() =>
    [
        new(
            Text: LocalizationService.Instance.GetString("Str_Tiling_SingleMonitorGrid"),
            IconSymbol: SymbolRegular.Grid24,
            OnClick: () => InvokeOnUiDispatcher(() => _onTileRunningInstances?.Invoke(WindowTilingMode.SingleMonitorGrid))),
        new(
            Text: LocalizationService.Instance.GetString("Str_Tiling_SpreadAcrossMonitors"),
            IconSymbol: SymbolRegular.Desktop24,
            OnClick: () => InvokeOnUiDispatcher(() => _onTileRunningInstances?.Invoke(WindowTilingMode.SpreadAcrossMonitors))),
        new(
            Text: LocalizationService.Instance.GetString("Str_Tiling_MainAndSide"),
            IconSymbol: SymbolRegular.LayoutRowTwo24,
            OnClick: () => InvokeOnUiDispatcher(() => _onTileRunningInstances?.Invoke(WindowTilingMode.MainAndSide)))
    ];

    private TrayMenuItemModel BuildServerLocationMenuItem()
    {
        var servers = _latestActiveServers;
        if (servers.Count == 0)
        {
            return new TrayMenuItemModel(
                Text: LastServerLocationText ?? LocalizationService.Instance.GetString("Str_Tray_ServerLocationNone"),
                IconSymbol: SymbolRegular.Globe24,
                IsEnabled: false);
        }

        if (servers.Count == 1)
        {
            var single = servers[0];
            var flag = ResolveFlagImageSource(single.Location?.Country);
            return new TrayMenuItemModel(
                Text: LastServerLocationText ?? FormatMultiSessionMenuItemText(single),
                IconSymbol: flag == null ? SymbolRegular.Globe24 : null,
                FlagImage: flag,
                OnClick: () => CopyServerSessionDetails(single));
        }

        var children = new List<TrayMenuItemModel>(servers.Count);
        foreach (var server in servers)
        {
            var captured = server;
            var flag = ResolveFlagImageSource(captured.Location?.Country);
            children.Add(new TrayMenuItemModel(
                Text: FormatMultiSessionMenuItemText(captured),
                IconSymbol: flag == null ? SymbolRegular.Globe24 : null,
                FlagImage: flag,
                OnClick: () => CopyServerSessionDetails(captured)));
        }

        return new TrayMenuItemModel(
            Text: LastServerLocationText ?? LocalizationService.Instance.Format("Str_Tray_ServerLocationMultiFormat", servers.Count),
            IconSymbol: SymbolRegular.Globe24,
            BadgeText: servers.Count.ToString(),
            Children: children);
    }

    private bool SafeIsFloatingBarVisible()
    {
        try
        {
            return _isFloatingBarVisible?.Invoke() == true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveAppVersionLabel()
    {
        var asm = typeof(NotifyIconWrapper).Assembly;
        string? infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            string clean = infoVer.Split('+')[0].Trim();
            if (!string.IsNullOrWhiteSpace(clean))
            {
                return clean.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? clean : $"v{clean}";
            }
        }

        Version? v = asm.GetName().Version;
        return v != null ? $"v{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "v1.0.0";
    }

    private static User32.RECT ResolveMonitorWorkAreaFromPoint(User32.POINT pt)
    {
        nint hMonitor = User32.MonitorFromPoint(pt, User32.MONITOR_DEFAULTTONEAREST);
        if (hMonitor != nint.Zero)
        {
            var mi = new User32.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<User32.MONITORINFOEX>(),
                szDevice = string.Empty
            };
            if (User32.GetMonitorInfoW(hMonitor, ref mi) &&
                mi.rcWork.Width > 0 &&
                mi.rcWork.Height > 0)
            {
                return mi.rcWork;
            }
        }

        int screenW = Math.Max(800, User32.GetSystemMetrics(User32.SM_CXSCREEN));
        int screenH = Math.Max(600, User32.GetSystemMetrics(User32.SM_CYSCREEN));
        return new User32.RECT { Left = 0, Top = 0, Right = screenW, Bottom = screenH };
    }

    private void EnsureTaskbarCreatedHook()
    {
        if (_taskbarCreatedMessageId == 0)
        {
            _taskbarCreatedMessageId = AppNativeMethods.RegisterWindowMessageW("TaskbarCreated");
        }
    }

    private void EnsureMessageOnlyHwndSource()
    {
        if (_headlessMode || _disposed || _hwndSource != null) return;

        try
        {
            var parameters = new HwndSourceParameters("BaBaBooeyStrap_TrayMsgWindow")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = new nint(-3) // HWND_MESSAGE
            };
            var source = new HwndSource(parameters);
            EnsureTaskbarCreatedHook();
            source.AddHook(WndProc);
            _hwndSource = source;
            _ownsHwndSource = true;
        }
        catch
        {
            // Non-fatal in threads/environments without message-only HWND support
        }
    }

    private void AttachHwndSource(nint hwnd)
    {
        if (_hwndSource != null && !_ownsHwndSource) return;
        EnsureTaskbarCreatedHook();

        if (_hwndSource != null && _ownsHwndSource)
        {
            try { _hwndSource.RemoveHook(WndProc); } catch { }
            try { _hwndSource.Dispose(); } catch { }
            _hwndSource = null;
            _ownsHwndSource = false;
        }

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
        _ownsHwndSource = false;
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
                _notifyIcon.MouseUp -= OnNotifyIconMouseUp;
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch { }
            _notifyIcon = null;
        }

        if (_components != null)
        {
            try { _components.Dispose(); } catch { }
            _components = null;
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
            if (_ownsHwndSource)
            {
                try { _hwndSource.Dispose(); } catch { }
            }
            _hwndSource = null;
            _ownsHwndSource = false;
        }

        if (_trayMenuWindow != null)
        {
            var menuWin = _trayMenuWindow;
            _trayMenuWindow = null;
            var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                if (dispatcher.CheckAccess())
                {
                    try { menuWin.ForceClose(); } catch { }
                }
                else
                {
                    try { dispatcher.BeginInvoke(() => { try { menuWin.ForceClose(); } catch { } }); } catch { }
                }
            }
            else
            {
                try { menuWin.ForceClose(); } catch { }
            }
        }

        DisposeWinFormsObjects();
        GC.SuppressFinalize(this);
    }
}
