using System.IO;
using System.Net.Http;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;

namespace BaBaBooeyStrap.App.Services.Pipeline;

public sealed class PackageDeploymentStep : IBootstrapPipelineStep
{
    private const long DiskSpaceBufferBytes = 256L * 1024 * 1024;

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
            if (!string.IsNullOrWhiteSpace(context.FailureReason))
            {
                return;
            }
        }

        string versionDir = !string.IsNullOrWhiteSpace(context.ResolvedVersionGuid)
            ? _paths.GetVersionDirectory(context.ResolvedVersionGuid)
            : VersionInstallationValidator.ResolveFallbackVersionDir(_paths, context.IsStudio);

        context.TargetVersionDirectory = versionDir;

        if (!string.IsNullOrWhiteSpace(versionDir) && Directory.Exists(versionDir))
        {
            string versionKey = Path.GetFileName(Path.TrimEndingDirectorySeparator(versionDir));
            var appState = context.IsStudio ? _robloxStateManager.Prop.Studio : _robloxStateManager.Prop.Player;
            IReadOnlyDictionary<string, string>? expectedHashes =
                string.Equals(appState.VersionGuid, versionKey, StringComparison.OrdinalIgnoreCase) && appState.PackageHashes.Count > 0
                    ? appState.PackageHashes
                    : null;

            VersionInstallationValidator.EnsureAppSettingsXml(versionDir, context.AppSettings.RobloxDomain, _logger);
            if (!context.IsStudio)
            {
                VersionInstallationValidator.TryRecoverMissingWebView2Loader(_paths, versionDir, _logger, expectedHashes);
            }

            if (!VersionInstallationValidator.IsVersionInstallationComplete(versionDir, context.IsStudio))
            {
                await VersionInstallationValidator.TryRepairFromCachedDownloadsAsync(
                    _paths,
                    versionDir,
                    context.IsStudio,
                    _logger,
                    expectedHashes,
                    ct).ConfigureAwait(false);
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
        string versionDownloadsDir = Path.Combine(_paths.Downloads, targetGuid);

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

            if (!TryCheckDiskSpace(packages, versionDownloadsDir, targetVersionDir, out string? spaceError))
            {
                _logger.Error("PackageDeploymentStep", spaceError ?? "Insufficient disk space for Roblox deployment.");
                context.FailureReason = spaceError;
                context.ResolvedVersionGuid = context.ExistingVersionGuid;
                return;
            }

            context.ReportStatus(LocalizationService.Instance.Format("Str_Bootstrapper_StatusDownloadingCountFormat", packages.Count));
            var downloader = new PackageDownloader();
            IProgress<PackageDownloadProgress>? downloadProgress = context.Progress == null
                ? null
                : new ProgressAdapter(p =>
                    context.Progress.Report(p with
                    {
                        ProgressPercentage = Math.Clamp(p.ProgressPercentage * 0.80, 0.0, 80.0),
                        IsExtracting = false
                    }));

            var downloadedPaths = await downloader.DownloadPackagesAsync(
                _httpClient,
                cdnBaseUrl,
                targetGuid,
                packages,
                versionDownloadsDir,
                downloadProgress,
                5,
                context.Channel,
                ct).ConfigureAwait(false);

            context.ReportStatus(LocalizationService.Instance.GetString("Str_Bootstrapper_StatusExtracting"));
            var extractor = new PackageExtractor();
            Directory.CreateDirectory(targetVersionDir);

            var extractWork = new List<(string PkgPath, string PkgName, string RelSubdir)>();
            for (int i = 0; i < packages.Count && i < downloadedPaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string pkgPath = downloadedPaths[i];
                if (string.IsNullOrWhiteSpace(pkgPath) || !File.Exists(pkgPath))
                {
                    throw new FileNotFoundException($"Downloaded package file missing for '{packages[i].Name}'.", pkgPath);
                }

                string pkgName = Path.GetFileName(pkgPath);
                if (!pkgName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relSubdir = PackageExtractor.PackageDirectoryMap.TryGetValue(pkgName, out var mapped)
                    ? mapped
                    : string.Empty;

                extractWork.Add((pkgPath, pkgName, relSubdir));
            }

            if (extractWork.Count > 0)
            {
                int totalExtract = extractWork.Count;
                int extractedCount = 0;
                long totalExtractBytes = packages.Sum(p => p.UncompressedSize > 0 ? p.UncompressedSize : p.CompressedSize);

                // Group by normalized target directory so shared-root packages serialize while disjoint subdirectories run in parallel
                var dirGroups = extractWork
                    .GroupBy(x => x.RelSubdir.Replace('\\', '/').Trim('/'), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                using var groupGate = new SemaphoreSlim(4, 4);
                var groupTasks = dirGroups.Select(async group =>
                {
                    await groupGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        foreach (var item in group)
                        {
                            ct.ThrowIfCancellationRequested();
                            await extractor.ExtractPackageAsync(item.PkgPath, targetVersionDir, item.RelSubdir, null, ct).ConfigureAwait(false);

                            int done = Interlocked.Increment(ref extractedCount);
                            double pct = 80.0 + ((double)done / totalExtract * 20.0);
                            context.Progress?.Report(new PackageDownloadProgress(
                                item.PkgName,
                                totalExtractBytes,
                                totalExtractBytes,
                                Math.Clamp(pct, 80.0, 100.0),
                                done,
                                totalExtract,
                                IsExtracting: true));
                        }
                    }
                    finally
                    {
                        groupGate.Release();
                    }
                });

                await Task.WhenAll(groupTasks).ConfigureAwait(false);
            }

            VersionInstallationValidator.EnsureAppSettingsXml(targetVersionDir, context.AppSettings.RobloxDomain, _logger);

            if (!File.Exists(targetExe) || !VersionInstallationValidator.IsVersionInstallationComplete(targetVersionDir, context.IsStudio))
            {
                string incompleteMsg = LocalizationService.Instance.Format("Str_Bootstrapper_ErrorDeploymentIncompleteFormat", targetGuid);
                _logger.Error("PackageDeploymentStep", incompleteMsg);
                context.FailureReason = incompleteMsg;
                context.ResolvedVersionGuid = context.ExistingVersionGuid;
                return;
            }

            var packageHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long totalSize = 0;
            foreach (var pkg in packages)
            {
                if (!string.IsNullOrWhiteSpace(pkg.Name) && !string.IsNullOrWhiteSpace(pkg.Signature))
                {
                    packageHashes[pkg.Name] = pkg.Signature;
                }
                totalSize += pkg.UncompressedSize > 0 ? pkg.UncompressedSize : pkg.CompressedSize;
            }

            string latestVersion = context.LatestVersion ?? string.Empty;
            if (context.IsStudio)
            {
                _robloxStateManager.Prop.Studio.VersionGuid = targetGuid;
                _robloxStateManager.Prop.Studio.Version = latestVersion;
                _robloxStateManager.Prop.Studio.PackageHashes = packageHashes;
                _robloxStateManager.Prop.Studio.Size = totalSize;
            }
            else
            {
                _robloxStateManager.Prop.Player.VersionGuid = targetGuid;
                _robloxStateManager.Prop.Player.Version = latestVersion;
                _robloxStateManager.Prop.Player.PackageHashes = packageHashes;
                _robloxStateManager.Prop.Player.Size = totalSize;
            }

            _robloxStateManager.Save();
            _stateManager.Prop.ForceReinstall = false;
            _stateManager.Save();

            if (!context.AppSettings.DebugDisableVersionPackageCleanup)
            {
                PruneStaleVersionDownloads(targetGuid);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("PackageDeploymentStep", $"Deployment failed for '{targetGuid}': {ex.Message}");
            context.FailureException = ex;
            context.FailureReason = LocalizationService.Instance.Format("Str_Bootstrapper_ErrorDeploymentFailedFormat", ex.Message);
            context.ResolvedVersionGuid = context.ExistingVersionGuid;
        }
    }

    private bool TryCheckDiskSpace(
        IReadOnlyList<PackageRecord> packages,
        string versionDownloadsDir,
        string targetVersionDir,
        out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            long uncachedCompressedBytes = 0;
            long totalUncompressedBytes = 0;

            foreach (var pkg in packages)
            {
                long compressed = pkg.CompressedSize > 0 ? pkg.CompressedSize : pkg.UncompressedSize;
                long uncompressed = pkg.UncompressedSize > 0 ? pkg.UncompressedSize : compressed;
                totalUncompressedBytes += uncompressed;

                string cachedPath = Path.Combine(versionDownloadsDir, pkg.Name);
                if (!File.Exists(cachedPath))
                {
                    uncachedCompressedBytes += compressed;
                }
            }

            string? dlRoot = Path.GetPathRoot(Path.GetFullPath(versionDownloadsDir));
            string? verRoot = Path.GetPathRoot(Path.GetFullPath(targetVersionDir));
            if (string.IsNullOrWhiteSpace(dlRoot) || string.IsNullOrWhiteSpace(verRoot))
            {
                return true;
            }

            bool sameDrive = string.Equals(dlRoot, verRoot, StringComparison.OrdinalIgnoreCase);
            if (sameDrive)
            {
                var drive = new DriveInfo(verRoot);
                if (!drive.IsReady) return true;
                long required = uncachedCompressedBytes + totalUncompressedBytes + DiskSpaceBufferBytes;
                if (drive.AvailableFreeSpace < required)
                {
                    errorMessage = LocalizationService.Instance.Format(
                        "Str_Bootstrapper_ErrorDiskSpaceFormat",
                        verRoot,
                        Math.Ceiling(required / (1024.0 * 1024.0)),
                        Math.Floor(drive.AvailableFreeSpace / (1024.0 * 1024.0)));
                    return false;
                }
            }
            else
            {
                var dlDrive = new DriveInfo(dlRoot);
                long dlRequired = uncachedCompressedBytes + (DiskSpaceBufferBytes / 2);
                if (dlDrive.IsReady && dlDrive.AvailableFreeSpace < dlRequired)
                {
                    errorMessage = LocalizationService.Instance.Format(
                        "Str_Bootstrapper_ErrorDiskSpaceFormat",
                        dlRoot,
                        Math.Ceiling(dlRequired / (1024.0 * 1024.0)),
                        Math.Floor(dlDrive.AvailableFreeSpace / (1024.0 * 1024.0)));
                    return false;
                }

                var verDrive = new DriveInfo(verRoot);
                long verRequired = totalUncompressedBytes + DiskSpaceBufferBytes;
                if (verDrive.IsReady && verDrive.AvailableFreeSpace < verRequired)
                {
                    errorMessage = LocalizationService.Instance.Format(
                        "Str_Bootstrapper_ErrorDiskSpaceFormat",
                        verRoot,
                        Math.Ceiling(verRequired / (1024.0 * 1024.0)),
                        Math.Floor(verDrive.AvailableFreeSpace / (1024.0 * 1024.0)));
                    return false;
                }
            }
        }
        catch
        {
            // Do not block launch on UNC/virtual paths where DriveInfo is unsupported
        }

        return true;
    }

    private void PruneStaleVersionDownloads(string activeGuid)
    {
        try
        {
            if (!Directory.Exists(_paths.Downloads)) return;

            string playerGuid = _robloxStateManager.Prop.Player.VersionGuid;
            string studioGuid = _robloxStateManager.Prop.Studio.VersionGuid;

            foreach (string dir in Directory.GetDirectories(_paths.Downloads))
            {
                string name = Path.GetFileName(dir);
                if (!name.StartsWith("version-", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, activeGuid, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, playerGuid, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, studioGuid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
        catch { }
    }

    private sealed class ProgressAdapter(Action<PackageDownloadProgress> callback) : IProgress<PackageDownloadProgress>
    {
        public void Report(PackageDownloadProgress value) => callback(value);
    }
}
