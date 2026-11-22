using System.IO;
using System.Text;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;

namespace BaBaBooeyStrap.App.Services.Pipeline;

public sealed class ModsApplicationStep : IBootstrapPipelineStep
{
    private readonly IPaths _paths;
    private readonly IJsonManager<RobloxState> _robloxStateManager;
    private readonly ILogger _logger;

    public string StepName => "ModsApplication";

    public ModsApplicationStep(
        IPaths paths,
        IJsonManager<RobloxState> robloxStateManager,
        ILogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _robloxStateManager = robloxStateManager ?? throw new ArgumentNullException(nameof(robloxStateManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExecuteAsync(BootstrapPipelineContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        string versionDir = context.TargetVersionDirectory;
        if (string.IsNullOrWhiteSpace(versionDir) || !Directory.Exists(versionDir))
        {
            return;
        }

        context.ReportStatus(LocalizationService.Instance.GetString("Str_Bootstrapper_StatusApplyingMods"));

        try
        {
            var appSettings = context.AppSettings;
            string versionClientSettings = Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json");
            if (appSettings.UseFastFlagManager)
            {
                string modClientSettings = _paths.GetClientSettingsPath();
                if (File.Exists(modClientSettings))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(versionClientSettings)!);
                    string jsonContent = await File.ReadAllTextAsync(modClientSettings, Encoding.UTF8, ct).ConfigureAwait(false);
                    await File.WriteAllTextAsync(
                        versionClientSettings,
                        jsonContent,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        ct).ConfigureAwait(false);
                }
            }
            else if (File.Exists(versionClientSettings))
            {
                try
                {
                    var attr = File.GetAttributes(versionClientSettings);
                    if (attr.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(versionClientSettings, attr & ~FileAttributes.ReadOnly);
                    }
                    File.Delete(versionClientSettings);
                }
                catch (Exception ex)
                {
                    _logger.Warn("ModsApplicationStep", $"Failed to remove stale ClientAppSettings.json: {ex.Message}");
                }
            }

            var extractor = new PackageExtractor();
            var verifier = new PackageVerifier();
            string versionKey = Path.GetFileName(Path.TrimEndingDirectorySeparator(versionDir));
            var expectedHashes = context.IsStudio
                ? _robloxStateManager.Prop.Studio.PackageHashes
                : _robloxStateManager.Prop.Player.PackageHashes;

            async Task<PackageFileRestoreOutcome> RestorePackageFileAsync(string packageName, string packageRelativeFile)
            {
                string? zipPath = null;
                string versionedZip = !string.IsNullOrWhiteSpace(versionKey)
                    ? Path.Combine(_paths.Downloads, versionKey, packageName)
                    : string.Empty;

                if (!string.IsNullOrEmpty(versionedZip) && File.Exists(versionedZip))
                {
                    zipPath = versionedZip;
                }
                else
                {
                    string flatZip = Path.Combine(_paths.Downloads, packageName);
                    if (File.Exists(flatZip) &&
                        (expectedHashes == null ||
                         !expectedHashes.TryGetValue(packageName, out string? expectedMd5) ||
                         string.IsNullOrWhiteSpace(expectedMd5) ||
                         verifier.VerifyFileMd5(flatZip, expectedMd5)))
                    {
                        zipPath = flatZip;
                    }
                }

                if (string.IsNullOrWhiteSpace(zipPath))
                {
                    _logger.Warn("ModsApplicationStep", $"Cannot restore '{packageName}/{packageRelativeFile}': cached package zip unavailable.");
                    return PackageFileRestoreOutcome.CacheUnavailable;
                }

                string relSubdir = PackageExtractor.PackageDirectoryMap.TryGetValue(packageName, out string? mapped)
                    ? mapped
                    : string.Empty;

                int extracted = await extractor.ExtractPackageWithCountAsync(
                    zipPath,
                    versionDir,
                    relSubdir,
                    [packageRelativeFile],
                    ct).ConfigureAwait(false);

                return extracted > 0
                    ? PackageFileRestoreOutcome.Restored
                    : PackageFileRestoreOutcome.NotInPackage;
            }

            var modEngine = new ModOverlayEngine();
            var modResult = await modEngine.ApplyModificationsAsync(
                _paths.Modifications,
                versionDir,
                _robloxStateManager.Prop.ModManifest,
                RestorePackageFileAsync,
                excludeClientSettings: true,
                robloxDomain: appSettings.RobloxDomain,
                ct: ct).ConfigureAwait(false);

            if (modResult.Success)
            {
                _robloxStateManager.Prop.ModManifest = modResult.ManifestedFiles.ToList();
                _robloxStateManager.Save();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("ModsApplicationStep", $"Mod/FastFlag application error: {ex.Message}");
        }
    }
}
