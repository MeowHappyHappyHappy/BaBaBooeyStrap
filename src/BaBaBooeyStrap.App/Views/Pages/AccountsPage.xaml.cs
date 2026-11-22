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

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class AccountsPage : Page
{
    private readonly DispatcherTimer _statusTimer;
    private AccountsViewModel? _subscribedViewModel;

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
            ViewModel?.AccountManagement.RefreshMultiInstanceStatus();
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModelEvents(ViewModel?.AccountManagement);
        ViewModel?.AccountManagement.RefreshProtocolStatus();
        ViewModel?.AccountManagement.RefreshMultiInstanceStatus();
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

    private void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.AccountManagement.RefreshProtocolStatus();
        ViewModel?.AccountManagement.RefreshMultiInstanceStatus();
    }

    private void ImportMultiRoblox_Click(object sender, RoutedEventArgs e) => ViewModel?.AccountManagement.ImportFromMultiRoblox();

    private void MoveUp_Click(object sender, RoutedEventArgs e) => ViewModel?.AccountManagement.MoveSelectedAccount(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => ViewModel?.AccountManagement.MoveSelectedAccount(1);

    private void BatchAfkPreset_Click(object sender, RoutedEventArgs e) => ViewModel?.AccountManagement.ApplyBatchAfkPreset();

    private void EditSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsForSelectedAccount();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ViewModel?.AccountManagement.ToggleSelectAllAccounts();

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var dlg = new AddAccountDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.CreatedAccount != null)
        {
            ViewModel.AccountManagement.AddOrUpdateAccount(dlg.CreatedAccount);
            ViewModel.AccountManagement.SelectedAccountItem?.SetCookieHealth(AccountCookieHealth.Valid);
        }
    }

    private void UpdateCookie_Click(object sender, RoutedEventArgs e)
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
    }

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

    private void SaveTarget_Click(object sender, RoutedEventArgs e) => ViewModel?.AccountManagement.SaveCurrentJoinTarget();

    private void DeleteTarget_Click(object sender, RoutedEventArgs e) => ViewModel?.AccountManagement.DeleteSelectedJoinTarget();

    private void ClearLog_Click(object sender, RoutedEventArgs e) => ViewModel?.AccountManagement.ClearActivityLog();

    private void CancelLaunch_Click(object sender, RoutedEventArgs e) => ViewModel?.AccountManagement.CancelActiveLaunch();

    private void AccountsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not AccountItem item)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => ViewModel?.AccountManagement.CommitAccountNotesEdit(item));
    }

    private async void AccountsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
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

    private void AccountsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        bool hasSelection = ViewModel?.AccountManagement.SelectedAccountItem != null;
        bool notLaunching = ViewModel?.AccountManagement.IsNotLaunching ?? false;
        bool notVerifying = ViewModel?.AccountManagement.IsNotVerifyingCookies ?? false;

        CtxLaunchAccount.IsEnabled = hasSelection && notLaunching;
        CtxEditSettings.IsEnabled = hasSelection && notLaunching;
        CtxUpdateCookie.IsEnabled = hasSelection && notLaunching;
        CtxVerifyCookie.IsEnabled = hasSelection && notVerifying;
        CtxApplyAfk.IsEnabled = hasSelection && notLaunching;
        CtxRemoveAccount.IsEnabled = hasSelection && notLaunching;
    }

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
