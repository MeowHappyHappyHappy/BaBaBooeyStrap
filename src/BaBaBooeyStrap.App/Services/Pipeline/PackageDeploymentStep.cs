using System.IO;
using System.Net.Http;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;

namespace BaBaBooeyStrap.App.Services.Pipeline;

public sealed class PackageDeploymentStep : IBootstrapPipelineStep
{
    private readonly IPaths _paths;
    private readonly IJsonManager<State> _stateManager;
    private readonly IJsonManager<RobloxState> _robloxStateManager;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public string StepName => "PackageDeployment";

    public PackageDeploymentStep(
        IPaths paths,
        IJsonManager<State> stateManager,
        IJsonManager<RobloxState> robloxStateManager,
        HttpClient httpClient,
        ILogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        _robloxStateManager = robloxStateManager ?? throw new ArgumentNullException(nameof(robloxStateManager));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExecuteAsync(BootstrapPipelineContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (context.RequiresDownload &&
            !string.IsNullOrWhiteSpace(context.ResolvedVersionGuid) &&
            !string.IsNullOrWhiteSpace(context.CdnBaseUrl))
        {
            await DownloadAndExtractVersionAsync(context, ct).ConfigureAwait(false);
        }

        string versionDir = !string.IsNullOrWhiteSpace(context.ResolvedVersionGuid)
            ? _paths.GetVersionDirectory(context.ResolvedVersionGuid)
            : VersionInstallationValidator.ResolveFallbackVersionDir(_paths, context.IsStudio);

        context.TargetVersionDirectory = versionDir;

        if (!string.IsNullOrWhiteSpace(versionDir) && Directory.Exists(versionDir))
        {
            if (!VersionInstallationValidator.IsVersionInstallationComplete(versionDir, context.IsStudio))
            {
                VersionInstallationValidator.TryRepairFromCachedDownloads(_paths, versionDir, context.IsStudio, _logger);
            }

            VersionInstallationValidator.EnsureAppSettingsXml(versionDir, context.AppSettings.RobloxDomain, _logger);
            if (!context.IsStudio)
            {
                VersionInstallationValidator.TryRecoverMissingWebView2Loader(_paths, versionDir, _logger);
            }
        }
    }

    private async Task DownloadAndExtractVersionAsync(BootstrapPipelineContext context, CancellationToken ct)
    {
        string targetGuid = context.ResolvedVersionGuid!;
        string cdnBaseUrl = context.CdnBaseUrl!;
        string exeName = context.IsStudio ? "RobloxStudioBeta.exe" : "RobloxPlayerBeta.exe";
        string targetVersionDir = _paths.GetVersionDirectory(targetGuid);
        string targetExe = Path.Combine(targetVersionDir, exeName);

        try
        {
            context.ReportStatus(LocalizationService.Instance.Format("Str_Bootstrapper_StatusDownloadingManifestFormat", targetGuid));
            var manifestParser = new DeploymentManifestParser();
            var packages = await manifestParser.FetchAndParseAsync(
                cdnBaseUrl,
                targetGuid,
                _httpClient,
                context.Channel,
                ct: ct).ConfigureAwait(false);

            context.ReportStatus(LocalizationService.Instance.Format("Str_Bootstrapper_StatusDownloadingCountFormat", packages.Count));
            var downloader = new PackageDownloader();
            var downloadedPaths = await downloader.DownloadPackagesAsync(
                _httpClient,
                cdnBaseUrl,
                targetGuid,
                packages,
                _paths.Downloads,
                context.Progress,
                5,
                ct).ConfigureAwait(false);

            context.ReportStatus(LocalizationService.Instance.GetString("Str_Bootstrapper_StatusExtracting"));
            var extractor = new PackageExtractor();
            Directory.CreateDirectory(targetVersionDir);

            for (int i = 0; i < packages.Count && i < downloadedPaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string pkgPath = downloadedPaths[i];
                if (string.IsNullOrWhiteSpace(pkgPath) || !File.Exists(pkgPath)) continue;

                string pkgName = Path.GetFileName(pkgPath);
                string relSubdir = PackageExtractor.PackageDirectoryMap.TryGetValue(pkgName, out var mapped)
                    ? mapped
                    : string.Empty;

                if (pkgName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await extractor.ExtractPackageAsync(pkgPath, targetVersionDir, relSubdir, null, ct).ConfigureAwait(false);
                    }
                    catch (Exception pkgEx) when (pkgEx is not OperationCanceledException)
                    {
                        _logger.Warn("PackageDeploymentStep", $"Package extraction failed for '{pkgName}': {pkgEx.Message}");
                    }
                }
            }

            VersionInstallationValidator.EnsureAppSettingsXml(targetVersionDir, context.AppSettings.RobloxDomain, _logger);

            if (!File.Exists(targetExe))
            {
                context.ResolvedVersionGuid = context.ExistingVersionGuid;
                return;
            }

            string latestVersion = context.LatestVersion ?? string.Empty;
            if (context.IsStudio)
            {
                _robloxStateManager.Prop.Studio.VersionGuid = targetGuid;
                _robloxStateManager.Prop.Studio.Version = latestVersion;
            }
            else
            {
                _robloxStateManager.Prop.Player.VersionGuid = targetGuid;
                _robloxStateManager.Prop.Player.Version = latestVersion;
            }

            _robloxStateManager.Save();
            _stateManager.Prop.ForceReinstall = false;
            _stateManager.Save();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("PackageDeploymentStep", $"Update/deploy step fell back to local version: {ex.Message}");
            context.ResolvedVersionGuid = context.ExistingVersionGuid;
        }
    }
}
