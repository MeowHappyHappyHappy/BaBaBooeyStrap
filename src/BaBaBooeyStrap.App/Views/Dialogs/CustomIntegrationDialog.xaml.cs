using System.Windows;
using BaBaBooeyStrap.App.Services;
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
        var dlg = new OpenFileDialog
        {
            Filter = "Programs & Scripts (*.exe;*.bat;*.cmd;*.ps1)|*.exe;*.bat;*.cmd;*.ps1|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog() == true)
        {
            LocationBox.Text = dlg.FileName;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        string location = LocationBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location))
        {
            ErrorText.Text = LocalizationService.Instance.GetString("Str_IntegrationDialog_MissingFields");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        int delay = DelayBox.Value.HasValue
            ? Math.Clamp((int)DelayBox.Value.Value, 0, 60000)
            : 0;

        ResultIntegration = new CustomIntegration
        {
            Name = name,
            Location = location,
            LaunchArgs = ArgsBox.Text.Trim(),
            Delay = delay,
            PreLaunch = PreLaunchCheck.IsChecked == true,
            AutoClose = AutoCloseCheck.IsChecked == true
        };

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
