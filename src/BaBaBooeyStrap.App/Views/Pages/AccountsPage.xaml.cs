using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.Views.Dialogs;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class AccountsPage : Page
{
    private readonly DispatcherTimer _statusTimer;
    private MainWindowViewModel? _subscribedViewModel;

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
            ViewModel?.RefreshMultiInstanceStatus();
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModelEvents(ViewModel);
        ViewModel?.RefreshProtocolStatus();
        ViewModel?.RefreshMultiInstanceStatus();
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
            AttachViewModelEvents(ViewModel);
        }
    }

    private void AttachViewModelEvents(MainWindowViewModel? next)
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
        ViewModel?.RefreshProtocolStatus();
        ViewModel?.RefreshMultiInstanceStatus();
    }

    private void ImportMultiRoblox_Click(object sender, RoutedEventArgs e) => ViewModel?.ImportFromMultiRoblox();

    private void MoveUp_Click(object sender, RoutedEventArgs e) => ViewModel?.MoveSelectedAccount(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => ViewModel?.MoveSelectedAccount(1);

    private void BatchAfkPreset_Click(object sender, RoutedEventArgs e) => ViewModel?.ApplyBatchAfkPreset();

    private void EditSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsForSelectedAccount();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ViewModel?.ToggleSelectAllAccounts();

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var dlg = new AddAccountDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.CreatedAccount != null)
        {
            ViewModel.AddOrUpdateAccount(dlg.CreatedAccount);
        }
    }

    private void RemoveAccount_Click(object sender, RoutedEventArgs e) => ViewModel?.RemoveCheckedOrSelectedAccounts();

    private void SaveTarget_Click(object sender, RoutedEventArgs e) => ViewModel?.SaveCurrentJoinTarget();

    private void DeleteTarget_Click(object sender, RoutedEventArgs e) => ViewModel?.DeleteSelectedJoinTarget();

    private void ClearLog_Click(object sender, RoutedEventArgs e) => ViewModel?.ClearActivityLog();

    private void CancelLaunch_Click(object sender, RoutedEventArgs e) => ViewModel?.CancelActiveLaunch();

    private void AccountsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not AccountItem item)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => ViewModel?.CommitAccountNotesEdit(item));
    }

    private void AccountsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AccountsGrid.CurrentColumn is not null &&
            AccountsGrid.CurrentColumn.IsReadOnly &&
            ViewModel?.SelectedAccountItem is not null)
        {
            OpenSettingsForSelectedAccount();
        }
    }

    private void OpenSettingsForSelectedAccount()
    {
        if (ViewModel == null || ViewModel.IsLaunching) return;

        if (ViewModel.SelectedAccountItem is not AccountItem selected)
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
            ViewModel.UpdateAccountSettings(selected, dlg.ResultProfile);
        }
    }

    private async void LaunchSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        try
        {
            await ViewModel.LaunchSelectedFromUiAsync();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("AccountsPage.LaunchSelected", ex);
            ViewModel.AppendActivityLog($"Launch failed: {ex.Message}");
        }
    }

    private async void LaunchChecked_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        try
        {
            await ViewModel.LaunchCheckedAccountsAsync();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("AccountsPage.LaunchChecked", ex);
            ViewModel.AppendActivityLog($"Batch launch failed: {ex.Message}");
        }
    }
}
