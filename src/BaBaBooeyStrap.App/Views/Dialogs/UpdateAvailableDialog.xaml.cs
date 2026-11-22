using System.Diagnostics;
using System.Windows;
using BaBaBooeyStrap.App.Services;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class UpdateAvailableDialog : FluentWindow
{
    private readonly AppUpdateInfo _updateInfo;
    private readonly IAppUpdateService _updateService;
    private CancellationTokenSource? _downloadCts;

    public string? SkippedVersion { get; private set; }
    public bool UpdateInstalledNeedsRestart { get; private set; }

    public UpdateAvailableDialog(AppUpdateInfo updateInfo, IAppUpdateService updateService)
    {
        _updateInfo = updateInfo ?? throw new ArgumentNullException(nameof(updateInfo));
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));

        InitializeComponent();

        VersionSummaryText.Text = LocalizationService.Instance.Format(
            "Str_Update_VersionSummaryFormat",
            _updateInfo.CurrentVersion.ToString(3),
            _updateInfo.LatestVersion.ToString(3));

        ReleaseNotesBox.Text = string.IsNullOrWhiteSpace(_updateInfo.ReleaseNotes)
            ? LocalizationService.Instance.GetString("Str_Update_NoReleaseNotes")
            : _updateInfo.ReleaseNotes;

        if (string.IsNullOrWhiteSpace(_updateInfo.AssetDownloadUrl))
        {
            UpdateNowButton.Content = LocalizationService.Instance.GetString("Str_Update_OpenDownloadPage");
        }

        Closed += (_, _) => _downloadCts?.Cancel();
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_updateInfo.AssetDownloadUrl))
        {
            OpenUrlSafe(_updateInfo.HtmlUrl);
            DialogResult = true;
            Close();
            return;
        }

        UpdateNowButton.IsEnabled = false;
        OpenGitHubButton.IsEnabled = false;
        SkipVersionButton.IsEnabled = false;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        ProgressStatusText.Text = LocalizationService.Instance.GetString("Str_Update_Downloading");

        _downloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(ratio =>
            {
                double pct = Math.Clamp(ratio * 100.0, 0.0, 100.0);
                DownloadProgressBar.Value = pct;
                ProgressStatusText.Text = LocalizationService.Instance.Format("Str_Update_DownloadingPctFormat", pct);
            });

            string? scriptPath = await _updateService.DownloadAndScheduleInstallAsync(_updateInfo, progress, _downloadCts.Token);
            if (!string.IsNullOrWhiteSpace(scriptPath))
            {
                UpdateInstalledNeedsRestart = true;
                DialogResult = true;
                Close();
                return;
            }

            OpenUrlSafe(_updateInfo.HtmlUrl);
            Close();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ProgressStatusText.Text = LocalizationService.Instance.Format("Str_Update_FailedFormat", ex.Message);
            UpdateNowButton.IsEnabled = true;
            OpenGitHubButton.IsEnabled = true;
            SkipVersionButton.IsEnabled = true;
        }
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        OpenUrlSafe(_updateInfo.HtmlUrl);
    }

    private void SkipVersion_Click(object sender, RoutedEventArgs e)
    {
        SkippedVersion = _updateInfo.LatestVersion.ToString();
        DialogResult = false;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        DialogResult = false;
        Close();
    }

    private static void OpenUrlSafe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
