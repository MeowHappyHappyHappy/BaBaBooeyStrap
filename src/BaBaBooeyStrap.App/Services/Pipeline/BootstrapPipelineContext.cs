using BaBaBooeyStrap.Bootstrapper.Deployment.Models;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.App.Services.Pipeline;

public sealed class BootstrapPipelineContext
{
    public BootstrapPipelineContext(
        LaunchSettings launchSettings,
        Settings appSettings,
        bool isStudio,
        IProgress<PackageDownloadProgress>? progress = null,
        Action<string>? statusCallback = null)
    {
        LaunchSettings = launchSettings ?? throw new ArgumentNullException(nameof(launchSettings));
        AppSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        IsStudio = isStudio;
        Progress = progress;
        StatusCallback = statusCallback;
        Channel = appSettings.Channel;
    }

    public LaunchSettings LaunchSettings { get; }
    public Settings AppSettings { get; }
    public bool IsStudio { get; }
    public IProgress<PackageDownloadProgress>? Progress { get; }
    public Action<string>? StatusCallback { get; }

    public string? ExistingVersionGuid { get; set; }
    public string? ResolvedVersionGuid { get; set; }
    public string? LatestVersion { get; set; }
    public string? CdnBaseUrl { get; set; }
    public string Channel { get; set; }
    public bool RequiresDownload { get; set; }
    public string TargetVersionDirectory { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public Exception? FailureException { get; set; }

    public void ReportStatus(string message) => StatusCallback?.Invoke(message);
}
