using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.Views.Dialogs;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using Microsoft.Win32;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class BehaviourPage : Page
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public BehaviourPage()
    {
        InitializeComponent();
    }

    private async void CleanNow_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var owner = Window.GetWindow(this);
        bool confirmed = FluentDialogService.ShowConfirm(
            LocalizationService.Instance.GetString("Str_Behaviour_RunCleanerNow"),
            LocalizationService.Instance.GetString("Str_Behaviour_CleanNowConfirm"),
            isDestructive: true,
            owner: owner);
        if (!confirmed) return;

        try
        {
            await ViewModel.WindowIntegrations.RunCleanerNowAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("BehaviourPage.CleanNow_Click", ex);
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                owner);
        }
    }

    private void AddIntegration_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var dlg = new CustomIntegrationDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.ResultIntegration != null)
        {
            ViewModel.WindowIntegrations.AddCustomIntegration(dlg.ResultIntegration);
        }
    }

    private void EditIntegration_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || sender is not FrameworkElement { DataContext: CustomIntegration existing })
        {
            return;
        }

        int idx = ViewModel.WindowIntegrations.CustomIntegrations.IndexOf(existing);
        if (idx < 0) return;

        var dlg = new CustomIntegrationDialog(existing) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.ResultIntegration != null)
        {
            ViewModel.WindowIntegrations.UpdateCustomIntegration(idx, dlg.ResultIntegration);
        }
    }

    private void RemoveIntegration_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || sender is not FrameworkElement { DataContext: CustomIntegration existing })
        {
            return;
        }

        var owner = Window.GetWindow(this);
        bool confirmed = FluentDialogService.ShowConfirm(
            LocalizationService.Instance.GetString("Str_Common_Remove"),
            LocalizationService.Instance.Format("Str_Behaviour_RemoveIntegrationConfirmFormat", existing.Name),
            isDestructive: true,
            owner: owner);
        if (!confirmed) return;

        ViewModel.WindowIntegrations.RemoveCustomIntegration(existing);
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        var owner = Window.GetWindow(this);
        var dlg = new SaveFileDialog
        {
            Title = LocalizationService.Instance.GetString("Str_Behaviour_ExportProfile"),
            Filter = LocalizationService.Instance.GetString("Str_Filter_ProfileJson"),
            FileName = $"BaBaBooeyStrap_Profile_{DateTime.Now:yyyyMMdd}.json"
        };

        if ((owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) != true) return;

        try
        {
            ViewModel.ExportProfileToFile(dlg.FileName);
            FluentDialogService.ShowInfo(
                LocalizationService.Instance.GetString("Str_Behaviour_ProfileHeader"),
                LocalizationService.Instance.Format("Str_Behaviour_ExportProfileSuccessFormat", dlg.FileName),
                owner);
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("BehaviourPage.ExportProfile_Click", ex);
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                owner);
        }
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        var owner = Window.GetWindow(this);
        var dlg = new OpenFileDialog
        {
            Title = LocalizationService.Instance.GetString("Str_Behaviour_ImportProfile"),
            Filter = LocalizationService.Instance.GetString("Str_Filter_ProfileJson")
        };

        if ((owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) != true) return;

        bool confirmed = FluentDialogService.ShowConfirm(
            LocalizationService.Instance.GetString("Str_Behaviour_ImportProfile"),
            LocalizationService.Instance.GetString("Str_Behaviour_ImportProfileConfirm"),
            owner: Window.GetWindow(this));
        if (!confirmed) return;

        try
        {
            ViewModel.ImportProfileFromFile(dlg.FileName);
            FluentDialogService.ShowInfo(
                LocalizationService.Instance.GetString("Str_Behaviour_ProfileHeader"),
                LocalizationService.Instance.GetString("Str_Behaviour_ImportProfileSuccess"),
                Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            FluentDialogService.ShowWarning(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                LocalizationService.Instance.Format("Str_Behaviour_ImportProfileFailedFormat", ex.Message),
                Window.GetWindow(this));
        }
    }
}
