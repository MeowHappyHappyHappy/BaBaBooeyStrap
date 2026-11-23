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
            FormatVersion(_updateInfo.CurrentVersion),
            FormatVersion(_updateInfo.LatestVersion));

        ReleaseNotesBox.Text = string.IsNullOrWhiteSpace(_updateInfo.ReleaseNotes)
            ? LocalizationService.Instance.GetString("Str_Update_NoReleaseNotes")
            : _updateInfo.ReleaseNotes;

        bool canAutoInstall = !string.IsNullOrWhiteSpace(_updateInfo.AssetDownloadUrl) &&
                              !string.IsNullOrWhiteSpace(_updateInfo.AssetSha256);
        if (!canAutoInstall)
        {
            UpdateNowButton.Content = LocalizationService.Instance.GetString("Str_Update_OpenDownloadPage");
        }

        Closed += (_, _) =>
        {
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _downloadCts = null;
        };
    }

    private static string FormatVersion(Version version) =>
        version.Build >= 0 ? version.ToString(3) : version.ToString(2);

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool canAutoInstall = !string.IsNullOrWhiteSpace(_updateInfo.AssetDownloadUrl) &&
                                  !string.IsNullOrWhiteSpace(_updateInfo.AssetSha256);
            if (!canAutoInstall)
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

            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _downloadCts = new CancellationTokenSource();

            var progress = new Progress<double>(ratio =>
            {
                double pct = Math.Clamp(ratio * 100.0, 0.0, 100.0);
                DownloadProgressBar.Value = pct;
                ProgressStatusText.Text = LocalizationService.Instance.Format("Str_Update_DownloadingPctFormat", pct);
            });

            string? result = await _updateService.DownloadAndScheduleInstallAsync(_updateInfo, progress, _downloadCts.Token);
            if (!string.IsNullOrWhiteSpace(result))
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
        if (!AppUpdateService.IsAllowedGitHubWebUrl(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
        }
        catch { }
    }
}
