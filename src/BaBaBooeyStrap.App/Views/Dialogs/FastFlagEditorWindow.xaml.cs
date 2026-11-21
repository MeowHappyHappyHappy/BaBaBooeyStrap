using System.IO;
using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
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

    private void ImportJson_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new JsonImportDialog { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            if (ViewModel.ImportJson(dialog.JsonText))
            {
                ShowBanner(LocalizationService.Instance.Format("Str_FastFlagEditor_MsgImportedFormat", ViewModel.TotalFlagCount), InfoBarSeverity.Success);
            }
            else
            {
                ShowBanner(LocalizationService.Instance.GetString("Str_FastFlagEditor_MsgInvalidJson"), InfoBarSeverity.Error);
            }
        }
    }

    private void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        string json = ViewModel.ExportJson();
        Clipboard.SetText(json);

        var sfd = new SaveFileDialog
        {
            Filter = LocalizationService.Instance.GetString("Str_FastFlagEditor_JsonFileFilter"),
            FileName = "ClientAppSettings.json"
        };

        if (sfd.ShowDialog() == true)
        {
            File.WriteAllText(sfd.FileName, json);
            ShowBanner(LocalizationService.Instance.Format("Str_FastFlagEditor_MsgExportedFileFormat", sfd.FileName), InfoBarSeverity.Success);
        }
        else
        {
            ShowBanner(LocalizationService.Instance.GetString("Str_FastFlagEditor_MsgExportedClipboard"), InfoBarSeverity.Success);
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearAll();
        ShowBanner(LocalizationService.Instance.GetString("Str_FastFlagEditor_MsgCleared"), InfoBarSeverity.Warning);
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetToDefaults();
        ShowBanner(LocalizationService.Instance.GetString("Str_FastFlagEditor_MsgResetDefaults"), InfoBarSeverity.Informational);
    }

    private void SaveAndClose_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveToManager();
        DialogResult = true;
        Close();
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
