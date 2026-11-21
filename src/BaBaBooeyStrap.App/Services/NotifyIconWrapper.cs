using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BaBaBooeyStrap.App.Interop;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Integrations.Matchmaking;
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
    private Dispatcher? _uiDispatcher;

    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _contextMenu;
    private WinForms.ToolStripMenuItem? _settingsMenuItem;
    private WinForms.ToolStripMenuItem? _launchMenuItem;
    private WinForms.ToolStripMenuItem? _multiInstanceMenuItem;
    private WinForms.ToolStripMenuItem? _serverLocationMenuItem;
    private WinForms.ToolStripMenuItem? _exitMenuItem;
    private ActiveServerSessionInfo? _singleActiveServer;
    private Drawing.Icon? _ownedIcon;
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

    public IReadOnlyList<string> ContextMenuActions { get; } =
        ["Settings", "Launch Roblox", "Multi-Instance Status", "Server Location", "Exit"];

    public NotifyIconWrapper(
        bool headlessMode = false,
        Action? onOpenSettings = null,
        Action? onLaunchRoblox = null,
        Func<MultiInstanceStatus>? getMultiInstanceStatus = null,
        Action? onToggleMultiInstance = null,
        Action? onExitRequested = null,
        Func<IReadOnlyList<ActiveServerSessionInfo>>? getActiveServers = null)
    {
        _headlessMode = headlessMode;
        _onOpenSettings = onOpenSettings;
        _onLaunchRoblox = onLaunchRoblox;
        _getMultiInstanceStatus = getMultiInstanceStatus;
        _onToggleMultiInstance = onToggleMultiInstance;
        _onExitRequested = onExitRequested;
        _getActiveServers = getActiveServers;
        _uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread);

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    public void Initialize(BootstrapperIcon iconKind = BootstrapperIcon.IconBloxstrap, string? customIconPath = null)
    {
        if (_disposed) return;

        _uiDispatcher ??= Application.Current?.Dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread);
        IsTrayActive = true;
        if (_headlessMode)
        {
            RefreshServerLocationStatus();
            return;
        }

        CreateOrRecreateWinFormsIcon(iconKind, customIconPath);
        EnsureTaskbarCreatedHook();
        RefreshServerLocationStatus();
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
            ? $"{RoValraService.FormatFlagEmoji(loc.Country)} {loc.City}, {loc.Region}, {loc.Country} ({loc.RegionCode})\nIP: {endpoint}"
            : LocalizationService.Instance.Format("Str_Tray_ServerJoinIpOnlyFormat", endpoint);

        return (title, message);
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
            return;
        }

        if (_settingsMenuItem != null)
            _settingsMenuItem.Text = LocalizationService.Instance.GetString("Str_Tray_Settings");

        if (_launchMenuItem != null)
            _launchMenuItem.Text = LocalizationService.Instance.GetString("Str_Tray_LaunchRoblox");

        if (_multiInstanceMenuItem != null)
        {
            _multiInstanceMenuItem.Text = LocalizationService.Instance.GetString("Str_Tray_MultiInstanceStatus");
            RefreshMultiInstanceMenuStatus();
        }

        RefreshServerLocationStatusCore(forceMenuUpdate: true);

        if (_exitMenuItem != null)
            _exitMenuItem.Text = LocalizationService.Instance.GetString("Str_Tray_Exit");
    }

    public void RefreshServerLocationStatus()
    {
        RefreshServerLocationStatusCore(forceMenuUpdate: false);
    }

    private void RefreshServerLocationStatusCore(bool forceMenuUpdate)
    {
        if (_disposed) return;

        if (!_headlessMode)
        {
            var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                try { dispatcher.BeginInvoke(new Action(() => RefreshServerLocationStatusCore(forceMenuUpdate))); } catch { }
                return;
            }

            if (_contextMenu != null && _contextMenu.IsHandleCreated && _contextMenu.InvokeRequired)
            {
                try { _contextMenu.BeginInvoke(new Action(() => RefreshServerLocationStatusCore(forceMenuUpdate))); } catch { }
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
                    _serverLocationMenuItem.Text = menuText;
                }
                if (_notifyIcon != null && !forceMenuUpdate)
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
                    _serverLocationMenuItem.Text = menuText;
                }
                if (_notifyIcon != null && !forceMenuUpdate)
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
                for (int i = 0; i < servers.Count; i++)
                {
                    var captured = servers[i];
                    var subItem = new WinForms.ToolStripMenuItem(
                        subLabels[i],
                        null,
                        (_, _) => CopyServerSessionDetails(captured));
                    _serverLocationMenuItem.DropDownItems.Add(subItem);
                }
                _serverLocationMenuItem.Text = multiMenuText;
            }
            if (_notifyIcon != null && !forceMenuUpdate)
            {
                try { _notifyIcon.Text = multiTooltip; } catch { }
            }
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
            string flag = RoValraService.FormatFlagEmoji(loc.Country);
            return includeRegionDetail
                ? $"{flag} {loc.City}, {loc.Region}, {loc.Country} ({loc.RegionCode})"
                : $"{flag} {loc.City}, {loc.Country} ({loc.RegionCode})";
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
                CreateOrRecreateWinFormsIcon(BootstrapperIcon.IconBloxstrap, null);
            }
        }
        else
        {
            CreateOrRecreateWinFormsIcon(BootstrapperIcon.IconBloxstrap, null);
        }
    }

    private void CreateOrRecreateWinFormsIcon(BootstrapperIcon iconKind, string? customIconPath)
    {
        DisposeWinFormsObjects();

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

        _settingsMenuItem = null;
        _launchMenuItem = null;
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
