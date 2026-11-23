using System.IO;
using System.Text;
using System.Windows;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.Logging;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class JsonImportDialog : FluentWindow
{
    private const int MaxImportBytes = 2 * 1024 * 1024;

    public string JsonText { get; private set; } = string.Empty;

    public JsonImportDialog()
    {
        InitializeComponent();
    }

    private void LoadFile_Click(object sender, RoutedEventArgs e)
    {
        ClearError();
        var ofd = new OpenFileDialog
        {
            Filter = LocalizationService.Instance.GetString("Str_FastFlagEditor_JsonFileFilter")
        };

        if (ofd.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var info = new FileInfo(ofd.FileName);
            if (!info.Exists || info.Length > MaxImportBytes)
            {
                ShowError(LocalizationService.Instance.GetString("Str_JsonImport_ErrTooLarge"));
                return;
            }

            JsonInputBox.Text = File.ReadAllText(ofd.FileName);
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("JsonImportDialog.LoadFile", ex);
            ShowError(LocalizationService.Instance.Format("Str_JsonImport_ErrFileReadFormat", ex.Message));
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        ClearError();
        string text = JsonInputBox.Text ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(text) > MaxImportBytes)
        {
            ShowError(LocalizationService.Instance.GetString("Str_JsonImport_ErrTooLarge"));
            return;
        }

        JsonText = text;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }
}
