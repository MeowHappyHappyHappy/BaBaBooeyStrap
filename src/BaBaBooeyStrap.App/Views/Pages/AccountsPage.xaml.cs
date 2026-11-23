using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.ViewModels.Sections;
using BaBaBooeyStrap.App.Views.Dialogs;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class AccountsPage : Page
{
    private const double NarrowPageThreshold = 820;
    private const double WidePageThreshold = 850;
    private const double CompactHeightThreshold = 580;
    private const double ExpandedHeightThreshold = 640;

    private readonly DispatcherTimer _statusTimer;
    private AccountsViewModel? _subscribedViewModel;
    private bool _isNarrowLayout;
    private bool _isControllerDetailsCollapsed;
    private bool _userToggledControllerDetails;
    private bool _isRunningStripCollapsed;
    private bool _isActivityLogCollapsed;
    private double _expandedLogHeight = 145;

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public AccountsPage()
    {
        InitializeComponent();

        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _statusTimer.Tick += (_, _) =>
        {
            try
            {
                ViewModel?.AccountManagement.RefreshMultiInstanceStatus();
            }
            catch (Exception ex)
            {
                Logger.ExceptionStatic("AccountsPage.StatusTimerTick", ex);
            }
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        SizeChanged += OnPageSizeChanged;
        AccountsCard.SizeChanged += OnAccountsCardSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModelEvents(ViewModel?.AccountManagement);
        ViewModel?.AccountManagement.RefreshProtocolStatus();
        ViewModel?.AccountManagement.RefreshMultiInstanceStatus();
        ViewModel?.AccountManagement.RefreshRunningInstances();
        if (ViewModel?.AccountManagement is { } accVm)
        {
            _ = accVm.BackfillMissingTargetThumbnailsAsync();
        }
        UpdateResponsiveLayout(ActualWidth, ActualHeight, forceApply: true);
        UpdateResponsiveGridColumns(AccountsCard.ActualWidth);
        _statusTimer.Start();
        LogTextBox.ScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _statusTimer.Stop();
        AttachViewModelEvents(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            AttachViewModelEvents(ViewModel?.AccountManagement);
        }
    }

    private void AttachViewModelEvents(AccountsViewModel? next)
    {
        if (ReferenceEquals(_subscribedViewModel, next))
        {
            return;
        }

        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.ActivityLogAppended -= OnActivityLogAppended;
        }

        _subscribedViewModel = next;
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.ActivityLogAppended += OnActivityLogAppended;
        }
    }

    private void OnActivityLogAppended(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => LogTextBox.ScrollToEnd());
            return;
        }

        LogTextBox.ScrollToEnd();
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
    }

    private void OnAccountsCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateResponsiveGridColumns(e.NewSize.Width);
    }

    private void UpdateResponsiveLayout(double pageWidth, double pageHeight, bool forceApply = false)
    {
        if (pageWidth <= 0 || pageHeight <= 0)
        {
            return;
        }

        bool nextNarrow = _isNarrowLayout
            ? pageWidth < WidePageThreshold
            : pageWidth < NarrowPageThreshold;

        if (nextNarrow != _isNarrowLayout || forceApply)
        {
            _isNarrowLayout = nextNarrow;
            ApplyWorkspaceLayoutMode();
        }

        if (!_userToggledControllerDetails)
        {
            if (pageHeight < CompactHeightThreshold && !_isControllerDetailsCollapsed)
            {
                _isControllerDetailsCollapsed = true;
                ApplyControllerDetailsCollapsedState();
            }
            else if (pageHeight >= ExpandedHeightThreshold && _isControllerDetailsCollapsed)
            {
                _isControllerDetailsCollapsed = false;
                ApplyControllerDetailsCollapsedState();
            }
        }
    }

    private void ApplyWorkspaceLayoutMode()
    {
        if (_isNarrowLayout)
        {
            PageScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            RootLayoutGrid.Margin = new Thickness(12, 10, 12, 10);
            WorkspaceRow.MinHeight = 0;
            WorkspaceRow.Height = GridLength.Auto;

            TargetColumn.Width = new GridLength(0);
            WorkspaceRow0.Height = GridLength.Auto;
            WorkspaceRow1.Height = GridLength.Auto;

            Grid.SetRow(AccountsCard, 0);
            Grid.SetColumn(AccountsCard, 0);
            AccountsCard.Margin = new Thickness(0, 0, 0, 10);

            Grid.SetRow(TargetPanelScroll, 1);
            Grid.SetColumn(TargetPanelScroll, 0);
            TargetPanelScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

            AccountsGrid.MinHeight = 200;
            AccountsGrid.MaxHeight = 360;
        }
        else
        {
            PageScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            RootLayoutGrid.Margin = new Thickness(20, 12, 20, 12);
            WorkspaceRow.MinHeight = 220;
            WorkspaceRow.Height = new GridLength(1, GridUnitType.Star);

            TargetColumn.Width = new GridLength(310);
            WorkspaceRow0.Height = new GridLength(1, GridUnitType.Star);
            WorkspaceRow1.Height = GridLength.Auto;

            Grid.SetRow(AccountsCard, 0);
            Grid.SetColumn(AccountsCard, 0);
            AccountsCard.Margin = new Thickness(0, 0, 12, 0);

            Grid.SetRow(TargetPanelScroll, 0);
            Grid.SetColumn(TargetPanelScroll, 1);
            TargetPanelScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            AccountsGrid.MinHeight = 0;
            AccountsGrid.MaxHeight = double.PositiveInfinity;
        }

        ApplyActivityLogCollapsedState();
    }

    private void UpdateResponsiveGridColumns(double cardWidth)
    {
        if (cardWidth <= 0)
        {
            return;
        }

        SettingsSummaryColumn.Visibility = cardWidth >= 720 ? Visibility.Visible : Visibility.Collapsed;
        UserIdColumn.Visibility = cardWidth >= 620 ? Visibility.Visible : Visibility.Collapsed;
        LastLaunchedColumn.Visibility = cardWidth >= 540 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyControllerDetailsCollapsedState()
    {
        ControllerDetailsPanel.Visibility = _isControllerDetailsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ControllerDetailsToggle.Content = _isControllerDetailsCollapsed ? "▸" : "▾";
    }

    private void ApplyRunningStripCollapsedState()
    {
        RunningInstancesItems.Visibility = _isRunningStripCollapsed ? Visibility.Collapsed : Visibility.Visible;
        RunningStripToggle.Content = _isRunningStripCollapsed ? "▸" : "▾";
        RunningStripHeaderGrid.Margin = _isRunningStripCollapsed ? new Thickness(0) : new Thickness(0, 0, 0, 6);
    }

    private void ApplyActivityLogCollapsedState()
    {
        if (_isActivityLogCollapsed)
        {
            if (LogRow.Height.IsAbsolute && LogRow.Height.Value >= 90)
            {
                _expandedLogHeight = LogRow.Height.Value;
            }

            LogTextBox.Visibility = Visibility.Collapsed;
            ActivityLogSplitter.Visibility = Visibility.Collapsed;
            LogSplitterRow.Height = new GridLength(0);
            LogRow.MinHeight = 0;
            LogRow.Height = GridLength.Auto;
            ActivityLogHeaderGrid.Margin = new Thickness(0);
            ActivityLogToggle.Content = "▸";
            return;
        }

        LogTextBox.Visibility = Visibility.Visible;
        ActivityLogHeaderGrid.Margin = new Thickness(0, 0, 0, 6);
        ActivityLogToggle.Content = "▾";
        LogRow.MinHeight = 90;

        if (_isNarrowLayout)
        {
            ActivityLogSplitter.Visibility = Visibility.Collapsed;
            LogSplitterRow.Height = new GridLength(0);
            LogRow.Height = new GridLength(130);
        }
        else
        {
            ActivityLogSplitter.Visibility = Visibility.Visible;
            LogSplitterRow.Height = new GridLength(8);
            LogRow.Height = new GridLength(Math.Max(90, _expandedLogHeight));
        }
    }

    private void ToggleControllerDetails_Click(object sender, RoutedEventArgs e)
    {
        _userToggledControllerDetails = true;
        _isControllerDetailsCollapsed = !_isControllerDetailsCollapsed;
        ApplyControllerDetailsCollapsedState();
    }

    private void ToggleRunningStrip_Click(object sender, RoutedEventArgs e)
    {
        _isRunningStripCollapsed = !_isRunningStripCollapsed;
        ApplyRunningStripCollapsedState();
    }

    private void ToggleActivityLog_Click(object sender, RoutedEventArgs e)
    {
        _isActivityLogCollapsed = !_isActivityLogCollapsed;
        ApplyActivityLogCollapsedState();
    }

    private void RefreshStatus_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.RefreshStatus", () =>
        {
            ViewModel?.AccountManagement.RefreshProtocolStatus();
            ViewModel?.AccountManagement.RefreshMultiInstanceStatus();
            ViewModel?.AccountManagement.RefreshRunningInstances();
        });

    private void QuickSwitcher_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.QuickSwitcher", () => ViewModel?.AccountManagement.ShowQuickSwitcher());

    private void ToggleFloatingBar_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.ToggleFloatingBar", () => ViewModel?.AccountManagement.ToggleFloatingBar());

    private void TileMenu_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.TileMenu", () =>
        {
            if (ViewModel == null) return;
            var monitors = ViewModel.AccountManagement.AvailableMonitors;

            var menu = new ContextMenu();
            if (monitors.Count <= 1)
            {
                var grid = new MenuItem { Header = LocalizationService.Instance.GetString("Str_Tiling_SingleMonitorGrid") };
                grid.Click += (_, _) => RunSafe("AccountsPage.TileSingleMonitor", () =>
                    ViewModel.AccountManagement.TileRunningWindows(WindowTilingMode.SingleMonitorGrid));
                menu.Items.Add(grid);
            }
            else
            {
                var gridSub = new MenuItem { Header = LocalizationService.Instance.GetString("Str_Tiling_SingleMonitorGrid") };
                var autoItem = new MenuItem { Header = LocalizationService.Instance.GetString("Str_Tiling_MonitorCursorAuto") };
                autoItem.Click += (_, _) => RunSafe("AccountsPage.TileMonitorAuto", () =>
                    ViewModel.AccountManagement.TileRunningWindows(WindowTilingMode.SingleMonitorGrid, null));
                gridSub.Items.Add(autoItem);
                gridSub.Items.Add(new Separator());
                foreach (var mon in monitors)
                {
                    var captured = mon;
                    var mItem = new MenuItem { Header = mon.DisplayLabel };
                    mItem.Click += (_, _) => RunSafe("AccountsPage.TileMonitorSpecific", () =>
                        ViewModel.AccountManagement.TileRunningWindows(WindowTilingMode.SingleMonitorGrid, captured.DeviceName));
                    gridSub.Items.Add(mItem);
                }
                menu.Items.Add(gridSub);

                var spread = new MenuItem { Header = LocalizationService.Instance.GetString("Str_Tiling_SpreadAcrossMonitors") };
                spread.Click += (_, _) => RunSafe("AccountsPage.TileSpread", () =>
                    ViewModel.AccountManagement.TileRunningWindows(WindowTilingMode.SpreadAcrossMonitors));
                menu.Items.Add(spread);

                var mainSide = new MenuItem { Header = LocalizationService.Instance.GetString("Str_Tiling_MainAndSide") };
                mainSide.Click += (_, _) => RunSafe("AccountsPage.TileMainAndSide", () =>
                    ViewModel.AccountManagement.TileRunningWindows(WindowTilingMode.MainAndSide));
                menu.Items.Add(mainSide);
            }

            menu.PlacementTarget = TileWindowsButton;
            menu.IsOpen = true;
        });

    private void SwitcherSettings_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.SwitcherSettings", () =>
        {
            if (ViewModel == null) return;
            var dlg = new WindowSwitcherSettingsDialog(
                ViewModel.Settings,
                ViewModel.AccountManagement.SwitcherCoordinator,
                ViewModel.SettingsManager)
            {
                Owner = Window.GetWindow(this)
            };
            dlg.ShowDialog();
        });

    private void MinimizeAllRunning_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.MinimizeAllRunning", () => ViewModel?.AccountManagement.MinimizeAllRunningInstances());

    private void RunningInstancePill_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.FocusRunningInstance", () =>
        {
            if (sender is FrameworkElement { Tag: int pid })
            {
                ViewModel?.AccountManagement.FocusRunningInstance(pid);
            }
        });

    private void RunningInstanceMoveMonitor_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.MoveMonitorMenu", () =>
        {
            if (ViewModel == null || sender is not FrameworkElement { Tag: int pid } el) return;
            var monitors = ViewModel.AccountManagement.AvailableMonitors;
            if (monitors.Count <= 1) return;

            var instance = ViewModel.AccountManagement.RunningInstances.FirstOrDefault(i => i.ProcessId == pid);
            var menu = new ContextMenu();
            foreach (var mon in monitors)
            {
                var captured = mon;
                var item = new MenuItem
                {
                    Header = mon.DisplayLabel,
                    IsChecked = instance != null && string.Equals(mon.DeviceName, instance.MonitorDeviceName, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += (_, _) => RunSafe("AccountsPage.MoveMonitor", () =>
                    ViewModel.AccountManagement.MoveRunningInstanceToMonitor(pid, captured.DeviceName));
                menu.Items.Add(item);
            }
            menu.PlacementTarget = el;
            menu.IsOpen = true;
        });

    private void RunningInstanceMinimize_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.ToggleMinimizeInstance", () =>
        {
            if (sender is FrameworkElement { Tag: int pid })
            {
                ViewModel?.AccountManagement.ToggleMinimizeRunningInstance(pid);
            }
        });

    private void RunningInstanceClose_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.CloseInstance", () =>
        {
            if (sender is FrameworkElement { Tag: int pid })
            {
                ViewModel?.AccountManagement.CloseRunningInstance(pid);
            }
        });

    private void ImportMultiRoblox_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.ImportMultiRoblox", () => ViewModel?.AccountManagement.ImportFromMultiRoblox());

    private void MoveUp_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.MoveUp", () => ViewModel?.AccountManagement.MoveSelectedAccount(-1));

    private void MoveDown_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.MoveDown", () => ViewModel?.AccountManagement.MoveSelectedAccount(1));

    private void BatchAfkPreset_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.BatchAfkPreset", () =>
        {
            if (ViewModel == null || ViewModel.AccountManagement.IsLaunching) return;

            int checkedCount = ViewModel.AccountManagement.GetCheckedAccounts().Count;
            int targetCount = checkedCount > 0
                ? checkedCount
                : (ViewModel.AccountManagement.SelectedAccountItem != null ? 1 : 0);

            if (targetCount > 0)
            {
                bool confirmed = FluentDialogService.ShowConfirm(
                    LocalizationService.Instance.GetString("Str_Accounts_BatchAfkConfirmTitle"),
                    LocalizationService.Instance.Format("Str_Accounts_BatchAfkConfirmMessageFormat", targetCount),
                    isDestructive: true,
                    owner: Window.GetWindow(this));
                if (!confirmed)
                {
                    return;
                }
            }

            ViewModel.AccountManagement.ApplyBatchAfkPreset();
        });

    private void EditSettings_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.EditSettings", OpenSettingsForSelectedAccount);

    private void SelectAll_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.SelectAll", () => ViewModel?.AccountManagement.ToggleSelectAllAccounts());

    private void AddAccount_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.AddAccount", () =>
        {
            if (ViewModel == null) return;
            var dlg = new AddAccountDialog { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.CreatedAccount != null)
            {
                ViewModel.AccountManagement.AddOrUpdateAccount(dlg.CreatedAccount);
                ViewModel.AccountManagement.SelectedAccountItem?.SetCookieHealth(AccountCookieHealth.Valid);
            }
        });

    private void UpdateCookie_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.UpdateCookie", () =>
        {
            if (ViewModel == null || ViewModel.AccountManagement.IsLaunching) return;

            if (ViewModel.AccountManagement.SelectedAccountItem is not AccountItem selected)
            {
                ViewModel.StatusMessage = LocalizationService.Instance.GetString("Str_Accounts_StatusSelectForCookieUpdate");
                return;
            }

            var dlg = new AddAccountDialog(selected.Username, selected.Notes) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.CreatedAccount != null)
            {
                ViewModel.AccountManagement.AddOrUpdateAccount(dlg.CreatedAccount);
                ViewModel.AccountManagement.SelectedAccountItem?.SetCookieHealth(AccountCookieHealth.Valid);
            }
        });

    private async void VerifyCookies_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        try
        {
            await ViewModel.AccountManagement.VerifyAccountsAsync();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("AccountsPage.VerifyCookies", ex);
            ViewModel.AccountManagement.AppendActivityLog(
                LocalizationService.Instance.Format("Str_Accounts_LogCookieVerificationFailedFormat", ex.Message));
        }
    }

    private async void VerifySelectedCookie_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.AccountManagement.SelectedAccountItem is not AccountItem selected) return;
        try
        {
            await ViewModel.AccountManagement.VerifyAccountsAsync([selected]);
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("AccountsPage.VerifySelectedCookie", ex);
            ViewModel.AccountManagement.AppendActivityLog(
                LocalizationService.Instance.Format("Str_Accounts_LogCookieVerificationFailedFormat", ex.Message));
        }
    }

    private void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || ViewModel.AccountManagement.IsLaunching) return;

        var candidates = ViewModel.AccountManagement.GetRemovalCandidates();
        if (candidates.Count == 0)
        {
            ViewModel.AccountManagement.RemoveCheckedOrSelectedAccounts();
            return;
        }

        string previewNames = string.Join(", ", candidates.Take(5).Select(a => a.Username));
        if (candidates.Count > 5)
        {
            previewNames += $" (+{candidates.Count - 5})";
        }

        bool confirmed = FluentDialogService.ShowConfirm(
            LocalizationService.Instance.GetString("Str_Dialog_ConfirmRemoveAccountTitle"),
            LocalizationService.Instance.Format("Str_Dialog_ConfirmRemoveAccountMessageFormat", candidates.Count, previewNames),
            confirmText: LocalizationService.Instance.GetString("Str_Common_Remove"),
            isDestructive: true,
            owner: Window.GetWindow(this));

        if (confirmed)
        {
            ViewModel.AccountManagement.RemoveCheckedOrSelectedAccounts();
        }
    }

    private void SaveTarget_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.SaveTarget", () => ViewModel?.AccountManagement.SaveCurrentJoinTarget());

    private void DeleteTarget_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.DeleteTarget", () =>
        {
            if (ViewModel == null) return;
            if (ViewModel.AccountManagement.ResolveTargetForDelete() is { } selected)
            {
                bool confirmed = FluentDialogService.ShowConfirm(
                    LocalizationService.Instance.GetString("Str_Accounts_DeleteTargetConfirmTitle"),
                    LocalizationService.Instance.Format("Str_Accounts_DeleteTargetConfirmMessageFormat", selected.Name),
                    confirmText: LocalizationService.Instance.GetString("Str_Accounts_DeleteTarget"),
                    isDestructive: true,
                    owner: Window.GetWindow(this));
                if (!confirmed)
                {
                    return;
                }
            }

            ViewModel.AccountManagement.DeleteSelectedJoinTarget();
        });

    private void ClearLog_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.ClearLog", () => ViewModel?.AccountManagement.ClearActivityLog());

    private void CancelLaunch_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.CancelLaunch", () => ViewModel?.AccountManagement.CancelActiveLaunch());

    private void AccountsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not AccountItem item)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => RunSafe("AccountsPage.CommitNotes", () => ViewModel?.AccountManagement.CommitAccountNotesEdit(item)));
    }

    private async void AccountsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.OriginalSource is DependencyObject source && FindVisualParent<CheckBox>(source) != null)
            {
                return;
            }

            if (ViewModel?.AccountManagement.SelectedAccountItem is null)
            {
                return;
            }

            if (ReferenceEquals(AccountsGrid.CurrentColumn, SettingsSummaryColumn))
            {
                OpenSettingsForSelectedAccount();
                return;
            }

            if (AccountsGrid.CurrentColumn is { IsReadOnly: true })
            {
                await LaunchSelectedWithConfirmationAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("AccountsPage.GridDoubleClick", ex);
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                Window.GetWindow(this));
        }
    }

    private void AccountsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var selected = ViewModel?.AccountManagement.SelectedAccountItem;
        bool hasSelection = selected != null;
        bool isRunningWithPid = selected is { IsRunning: true, RunningProcessId: > 0 };
        bool notLaunching = ViewModel?.AccountManagement.IsNotLaunching ?? false;
        bool notVerifying = ViewModel?.AccountManagement.IsNotVerifyingCookies ?? false;

        var windowActionVisibility = isRunningWithPid ? Visibility.Visible : Visibility.Collapsed;
        CtxFocusWindow.Visibility = windowActionVisibility;
        CtxFocusWindow.IsEnabled = isRunningWithPid;
        CtxCloseWindow.Visibility = windowActionVisibility;
        CtxCloseWindow.IsEnabled = isRunningWithPid;
        CtxWindowSeparator.Visibility = windowActionVisibility;

        CtxLaunchAccount.IsEnabled = hasSelection && notLaunching;
        CtxEditSettings.IsEnabled = hasSelection && notLaunching;
        CtxUpdateCookie.IsEnabled = hasSelection && notLaunching;
        CtxVerifyCookie.IsEnabled = hasSelection && notVerifying;
        CtxApplyAfk.IsEnabled = hasSelection && notLaunching;
        CtxRemoveAccount.IsEnabled = hasSelection && notLaunching;
    }

    private void CtxFocusWindow_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.CtxFocusWindow", () =>
        {
            if (ViewModel?.AccountManagement.SelectedAccountItem is { RunningProcessId: > 0 } item)
            {
                ViewModel.AccountManagement.FocusRunningInstance(item.RunningProcessId.Value);
            }
        });

    private void CtxCloseWindow_Click(object sender, RoutedEventArgs e) =>
        RunSafe("AccountsPage.CtxCloseWindow", () =>
        {
            if (ViewModel?.AccountManagement.SelectedAccountItem is { RunningProcessId: > 0 } item)
            {
                ViewModel.AccountManagement.CloseRunningInstance(item.RunningProcessId.Value);
            }
        });

    private void OpenSettingsForSelectedAccount()
    {
        if (ViewModel == null || ViewModel.AccountManagement.IsLaunching) return;

        if (ViewModel.AccountManagement.SelectedAccountItem is not AccountItem selected)
        {
            ViewModel.StatusMessage = LocalizationService.Instance.GetString("Str_Accounts_StatusSelectForSettings");
            return;
        }

        var dlg = new AccountSettingsDialog(selected.Username, selected.Account.RobloxSettings)
        {
            Owner = Window.GetWindow(this)
        };

        if (dlg.ShowDialog() == true)
        {
            ViewModel.AccountManagement.UpdateAccountSettings(selected, dlg.ResultProfile);
        }
    }

    private async void LaunchSelected_Click(object sender, RoutedEventArgs e) =>
        await LaunchSelectedWithConfirmationAsync();

    private async Task LaunchSelectedWithConfirmationAsync()
    {
        if (ViewModel == null || ViewModel.AccountManagement.IsLaunching) return;
        try
        {
            if (ViewModel.AccountManagement.SelectedAccountItem is AccountItem selected &&
                !ConfirmLaunchIfEnabled(1, selected.Username))
            {
                return;
            }

            await ViewModel.AccountManagement.LaunchSelectedFromUiAsync();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("AccountsPage.LaunchSelected", ex);
            ViewModel.AccountManagement.AppendActivityLog(
                LocalizationService.Instance.Format("Str_Accounts_LogLaunchFailedFormat", ex.Message));
        }
    }

    private async void LaunchChecked_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || ViewModel.AccountManagement.IsLaunching) return;
        try
        {
            var checkedAccounts = ViewModel.AccountManagement.GetCheckedAccounts();
            if (checkedAccounts.Count > 0)
            {
                string summary = string.Join(", ", checkedAccounts.Take(5).Select(a => a.Username));
                if (checkedAccounts.Count > 5)
                {
                    summary += $" (+{checkedAccounts.Count - 5})";
                }

                if (!ConfirmLaunchIfEnabled(checkedAccounts.Count, summary))
                {
                    return;
                }
            }

            await ViewModel.AccountManagement.LaunchCheckedAccountsAsync();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("AccountsPage.LaunchChecked", ex);
            ViewModel.AccountManagement.AppendActivityLog(
                LocalizationService.Instance.Format("Str_Accounts_LogBatchLaunchFailedFormat", ex.Message));
        }
    }

    private bool ConfirmLaunchIfEnabled(int accountCount, string accountSummary)
    {
        if (ViewModel == null || !ViewModel.LaunchSettings.ConfirmLaunches)
        {
            return true;
        }

        string target = string.IsNullOrWhiteSpace(ViewModel.AccountManagement.LaunchTargetInput)
            ? LocalizationService.Instance.GetString("Str_Accounts_ConfirmLaunchDefaultTarget")
            : ViewModel.AccountManagement.LaunchTargetInput.Trim();

        return FluentDialogService.ShowConfirm(
            LocalizationService.Instance.GetString("Str_Dialog_ConfirmLaunchTitle"),
            LocalizationService.Instance.Format("Str_Dialog_ConfirmLaunchAccountsMessageFormat", accountCount, accountSummary, target),
            confirmText: LocalizationService.Instance.GetString("Str_Common_Launch"),
            owner: Window.GetWindow(this));
    }

    private void RunSafe(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic(operation, ex);
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                Window.GetWindow(this));
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T match)
            {
                return match;
            }
            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }
}
