using System.IO;
using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class FastFlagEditorWindow : FluentWindow
{
    public FastFlagEditorViewModel ViewModel { get; }

    public FastFlagEditorWindow(FastFlagEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;
        InitializeComponent();
    }

    private void TypeFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TypeFilterBox.SelectedItem is not ComboBoxItem item) return;
        string tag = item.Tag?.ToString() ?? "All";
        ViewModel.SelectedTypeFilter = tag switch
        {
            "Boolean" => FastFlagType.Boolean,
            "Integer" => FastFlagType.Integer,
            "String" => FastFlagType.String,
            "Log" => FastFlagType.Log,
            "Unknown" => FastFlagType.Unknown,
            _ => null
        };
    }

    private void AddOrUpdateFlag_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.AddFlag(FlagNameBox.Text, FlagValueBox.Text);
            FlagNameBox.Text = string.Empty;
            FlagValueBox.Text = string.Empty;
            ShowBanner(LocalizationService.Instance.GetString("Str_FastFlagEditor_MsgFlagAdded"), InfoBarSeverity.Success);
        }
        catch (ArgumentException ex)
        {
            ShowBanner(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = FlagsDataGrid.SelectedItems.Cast<FastFlag>().ToList();
            if (selected.Count == 0)
            {
                ShowBanner(LocalizationService.Instance.GetString("Str_FastFlagEditor_MsgSelectToDelete"), InfoBarSeverity.Informational);
                return;
            }

            int deleted = 0;
            foreach (var flag in selected)
            {
                if (ViewModel.DeleteFlag(flag.Name)) deleted++;
            }
            ShowBanner(LocalizationService.Instance.Format("Str_FastFlagEditor_MsgDeletedFormat", deleted), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("FastFlagEditorWindow.DeleteSelected", ex);
            ShowBanner(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ImportJson_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new JsonImportDialog { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            bool replaceExisting = true;
            if (ViewModel.TotalFlagCount > 0)
            {
                var choice = FluentDialogService.ShowThreeChoice(
                    LocalizationService.Instance.GetString("Str_FastFlagEditor_ImportMergeTitle"),
                    LocalizationService.Instance.Format("Str_FastFlagEditor_ImportMergeMessageFormat", ViewModel.TotalFlagCount),
                    primaryText: LocalizationService.Instance.GetString("Str_FastFlagEditor_ImportMerge"),
                    secondaryText: LocalizationService.Instance.GetString("Str_FastFlagEditor_ImportReplace"),
                    cancelText: LocalizationService.Instance.GetString("Str_Common_Cancel"),
                    severity: FluentDialogSeverity.Question,
                    owner: this);

                if (choice == FluentDialogResult.Cancel)
                {
                    return;
                }

                replaceExisting = choice == FluentDialogResult.Secondary;
            }

            if (ViewModel.ImportJson(dialog.JsonText, replaceExisting))
            {
                ShowBanner(LocalizationService.Instance.Format("Str_FastFlagEditor_MsgImportedFormat", ViewModel.TotalFlagCount), InfoBarSeverity.Success);
            }
            else
            {
                ShowBanner(LocalizationService.Instance.GetString("Str_FastFlagEditor_MsgInvalidJson"), InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("FastFlagEditorWindow.ImportJson", ex);
            ShowBanner(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingGridEdits();
        string json = ViewModel.ExportJson();
        string? clipboardWarning = null;
        try
        {
            Clipboard.SetText(json);
        }
        catch (Exception ex)
        {
            clipboardWarning = LocalizationService.Instance.Format("Str_FastFlagEditor_MsgExportClipboardFailedFormat", ex.Message);
        }

        var sfd = new SaveFileDialog
        {
            Filter = LocalizationService.Instance.GetString("Str_FastFlagEditor_JsonFileFilter"),
            FileName = "ClientAppSettings.json"
        };

        if (sfd.ShowDialog(this) == true)
        {
            try
            {
                File.WriteAllText(sfd.FileName, json);
                string message = clipboardWarning == null
                    ? LocalizationService.Instance.Format("Str_FastFlagEditor_MsgExportedFileFormat", sfd.FileName)
                    : $"{LocalizationService.Instance.Format("Str_FastFlagEditor_MsgExportedFileFormat", sfd.FileName)} ({clipboardWarning})";
                ShowBanner(message, clipboardWarning == null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            }
            catch (Exception ex)
            {
                Logger.ExceptionStatic("FastFlagEditorWindow.ExportJson", ex);
                ShowBanner(LocalizationService.Instance.Format("Str_FastFlagEditor_MsgExportFileFailedFormat", ex.Message), InfoBarSeverity.Error);
            }
        }
        else if (clipboardWarning != null)
        {
            ShowBanner(clipboardWarning, InfoBarSeverity.Warning);
        }
        else
        {
            ShowBanner(LocalizationService.Instance.GetString("Str_FastFlagEditor_MsgExportedClipboard"), InfoBarSeverity.Success);
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel.TotalFlagCount > 0)
            {
                bool confirmed = FluentDialogService.ShowConfirm(
                    LocalizationService.Instance.GetString("Str_Dialog_ConfirmClearFlagsTitle"),
                    LocalizationService.Instance.Format("Str_Dialog_ConfirmClearFlagsMessageFormat", ViewModel.TotalFlagCount),
                    confirmText: LocalizationService.Instance.GetString("Str_FastFlagEditor_Clear"),
                    isDestructive: true,
                    owner: this);

                if (!confirmed) return;
            }

            ViewModel.ClearAll();
            ShowBanner(LocalizationService.Instance.GetString("Str_FastFlagEditor_MsgCleared"), InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("FastFlagEditorWindow.ClearAll", ex);
            ShowBanner(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool confirmed = FluentDialogService.ShowConfirm(
                LocalizationService.Instance.GetString("Str_Dialog_ConfirmResetFlagsTitle"),
                LocalizationService.Instance.GetString("Str_Dialog_ConfirmResetFlagsMessage"),
                confirmText: LocalizationService.Instance.GetString("Str_FastFlagEditor_ResetDefaults"),
                isDestructive: true,
                owner: this);

            if (!confirmed) return;

            ViewModel.ResetToDefaults();
            ShowBanner(LocalizationService.Instance.GetString("Str_FastFlagEditor_MsgResetDefaults"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("FastFlagEditorWindow.ResetDefaults", ex);
            ShowBanner(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void SaveAndClose_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitPendingGridEdits();
            ViewModel.SaveToManager();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("FastFlagEditorWindow.SaveAndClose", ex);
            ShowBanner(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void CommitPendingGridEdits()
    {
        FlagsDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        FlagsDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowBanner(string message, InfoBarSeverity severity)
    {
        StatusBanner.Title = severity == InfoBarSeverity.Error
            ? LocalizationService.Instance.GetString("Str_FastFlagEditor_BannerValidationError")
            : LocalizationService.Instance.GetString("Str_FastFlagEditor_BannerTitle");
        StatusBanner.Message = message;
        StatusBanner.Severity = severity;
        StatusBanner.IsOpen = true;
    }
}
