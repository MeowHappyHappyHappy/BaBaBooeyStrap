using System.IO;
using System.Windows;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class CustomIntegrationDialog : FluentWindow
{
    public CustomIntegration? ResultIntegration { get; private set; }

    public CustomIntegrationDialog(CustomIntegration? existing = null)
    {
        InitializeComponent();

        if (existing != null)
        {
            string editTitle = LocalizationService.Instance.GetString("Str_IntegrationDialog_EditTitle");
            Title = editTitle;
            DialogTitleBar.Title = editTitle;

            NameBox.Text = existing.Name;
            LocationBox.Text = existing.Location;
            ArgsBox.Text = existing.LaunchArgs;
            DelayBox.Value = Math.Clamp(existing.Delay, 0, 60000);
            PreLaunchCheck.IsChecked = existing.PreLaunch;
            AutoCloseCheck.IsChecked = existing.AutoClose;
        }

        Loaded += (_, _) => NameBox.Focus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new OpenFileDialog
            {
                Filter = LocalizationService.Instance.GetString("Str_Filter_ProgramsAndScripts")
            };

            if (dlg.ShowDialog(this) == true)
            {
                LocationBox.Text = dlg.FileName;
                if (string.IsNullOrWhiteSpace(NameBox.Text))
                {
                    NameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("CustomIntegrationDialog.Browse", ex);
            ShowError(ex.Message);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        string location = LocationBox.Text.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location))
        {
            ShowError(LocalizationService.Instance.GetString("Str_IntegrationDialog_MissingFields"));
            return;
        }

        if (!IsValidLocalExecutablePath(location))
        {
            ShowError(LocalizationService.Instance.GetString("Str_IntegrationDialog_InvalidLocation"));
            return;
        }

        int delay = DelayBox.Value.HasValue
            ? Math.Clamp((int)DelayBox.Value.Value, 0, 60000)
            : 0;

        ResultIntegration = new CustomIntegration
        {
            Name = name,
            Location = Path.GetFullPath(location),
            LaunchArgs = ArgsBox.Text.Trim(),
            Delay = delay,
            PreLaunch = PreLaunchCheck.IsChecked == true,
            AutoClose = AutoCloseCheck.IsChecked == true
        };

        DialogResult = true;
        Close();
    }

    private static bool IsValidLocalExecutablePath(string location)
    {
        if (location.StartsWith(@"\\", StringComparison.Ordinal) ||
            location.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme != Uri.UriSchemeFile)
        {
            return false;
        }

        if (!Path.IsPathRooted(location))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(location);
            return File.Exists(fullPath);
        }
        catch
        {
            return false;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
