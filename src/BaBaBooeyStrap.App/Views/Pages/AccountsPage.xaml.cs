using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
        }
    }

    private void RemoveAccount_Click(object sender, RoutedEventArgs e) => ViewModel?.AccountManagement.RemoveCheckedOrSelectedAccounts();

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

    private void AccountsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AccountsGrid.CurrentColumn is not null &&
            AccountsGrid.CurrentColumn.IsReadOnly &&
            ViewModel?.AccountManagement.SelectedAccountItem is not null)
        {
            OpenSettingsForSelectedAccount();
        }
    }

    private void OpenSettingsForSelectedAccount()
    {
        if (ViewModel == null || ViewModel.AccountManagement.IsLaunching) return;

        if (ViewModel.AccountManagement.SelectedAccountItem is not AccountItem selected)
        {
            ViewModel.StatusMessage = Services.LocalizationService.Instance.GetString("Str_Accounts_StatusSelectForSettings");
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

    private async void LaunchSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        try
        {
            await ViewModel.AccountManagement.LaunchSelectedFromUiAsync();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("AccountsPage.LaunchSelected", ex);
            ViewModel.AccountManagement.AppendActivityLog($"Launch failed: {ex.Message}");
        }
    }

    private async void LaunchChecked_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        try
        {
            await ViewModel.AccountManagement.LaunchCheckedAccountsAsync();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("AccountsPage.LaunchChecked", ex);
            ViewModel.AccountManagement.AppendActivityLog($"Batch launch failed: {ex.Message}");
        }
    }
}
