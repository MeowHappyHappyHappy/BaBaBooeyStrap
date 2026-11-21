using System.IO;
using System.Text;
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
            if (appSettings.UseFastFlagManager)
            {
                string modClientSettings = _paths.GetClientSettingsPath();
                if (File.Exists(modClientSettings))
                {
                    string versionClientSettings = _paths.GetClientSettingsPath(Path.GetFileName(versionDir));
                    Directory.CreateDirectory(Path.GetDirectoryName(versionClientSettings)!);
                    string jsonContent = await File.ReadAllTextAsync(modClientSettings, Encoding.UTF8, ct).ConfigureAwait(false);
                    await File.WriteAllTextAsync(
                        versionClientSettings,
                        jsonContent,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        ct).ConfigureAwait(false);
                }
            }

            var modEngine = new ModOverlayEngine();
            var modResult = await modEngine.ApplyModificationsAsync(
                _paths.Modifications,
                versionDir,
                _robloxStateManager.Prop.ModManifest,
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
