using System.IO;
using System.Windows;
using BaBaBooeyStrap.App.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class JsonImportDialog : FluentWindow
{
    public string JsonText { get; private set; } = string.Empty;

    public JsonImportDialog()
    {
        InitializeComponent();
    }

    private void LoadFile_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Filter = LocalizationService.Instance.GetString("Str_FastFlagEditor_JsonFileFilter")
        };

        if (ofd.ShowDialog() == true)
        {
            JsonInputBox.Text = File.ReadAllText(ofd.FileName);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        JsonText = JsonInputBox.Text;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
