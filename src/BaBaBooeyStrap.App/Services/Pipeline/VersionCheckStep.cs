using System.IO;
using System.Net.Http;
using System.Text.Json;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;

namespace BaBaBooeyStrap.App.Services.Pipeline;

public sealed class VersionCheckStep : IBootstrapPipelineStep
{
    private readonly IPaths _paths;
    private readonly IJsonManager<Settings> _settingsManager;
    private readonly IJsonManager<State> _stateManager;
    private readonly IJsonManager<RobloxState> _robloxStateManager;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ICdnMirrorSelector? _mirrorSelector;

    public string StepName => "VersionCheck";

    public VersionCheckStep(
        IPaths paths,
        IJsonManager<Settings> settingsManager,
        IJsonManager<State> stateManager,
        IJsonManager<RobloxState> robloxStateManager,
        HttpClient httpClient,
        ILogger logger,
        ICdnMirrorSelector? mirrorSelector = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        _robloxStateManager = robloxStateManager ?? throw new ArgumentNullException(nameof(robloxStateManager));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _mirrorSelector = mirrorSelector;
    }

    public async Task ExecuteAsync(BootstrapPipelineContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        bool isStudio = context.IsStudio;
        var launchSettings = context.LaunchSettings;
        string exeName = isStudio ? "RobloxStudioBeta.exe" : "RobloxPlayerBeta.exe";
        string existingGuid = isStudio
            ? _robloxStateManager.Prop.Studio.VersionGuid
            : _robloxStateManager.Prop.Player.VersionGuid;

        context.ExistingVersionGuid = existingGuid;
        context.ResolvedVersionGuid = existingGuid;
        context.RequiresDownload = false;

        bool force = launchSettings.ForceFlag.Active || _stateManager.Prop.ForceReinstall;
        if (!force && !string.IsNullOrWhiteSpace(existingGuid))
        {
            string existingDir = _paths.GetVersionDirectory(existingGuid);
            string existingExe = Path.Combine(existingDir, exeName);
            if (launchSettings.BypassUpdateCheck && File.Exists(existingExe))
            {
                return;
            }

            if (!_settingsManager.Prop.CheckForUpdates &&
                VersionInstallationValidator.IsVersionInstallationComplete(existingDir, isStudio))
            {
                return;
            }
        }

        if (launchSettings.BypassUpdateCheck)
        {
            return;
        }

        try
        {
            context.ReportStatus(LocalizationService.Instance.GetString("Str_Bootstrapper_StatusFindingCdn"));
            var selector = _mirrorSelector ?? new CdnMirrorSelector(_httpClient);
            string cdnBaseUrl = await selector.ResolveOptimalCdnUrlAsync(ct).ConfigureAwait(false);

            string channel = !string.IsNullOrWhiteSpace(launchSettings.ChannelFlag.Data)
                ? launchSettings.ChannelFlag.Data
                : _settingsManager.Prop.Channel;

            string binaryType = isStudio ? "WindowsStudio64" : "WindowsPlayer";
            string clientVersionUrl = $"https://clientsettings.roblox.com/v2/client-version/{binaryType}" +
                (!string.Equals(channel, "production", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(channel, "live", StringComparison.OrdinalIgnoreCase)
                    ? $"/channel/{Uri.EscapeDataString(channel)}"
                    : string.Empty);

            context.ReportStatus(LocalizationService.Instance.Format("Str_Bootstrapper_StatusCheckingVersionFormat", binaryType));
            using var resp = await _httpClient.GetAsync(clientVersionUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return;
            }

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            string latestGuid = doc.RootElement.GetProperty("clientVersionUpload").GetString() ?? string.Empty;
            string latestVersion = doc.RootElement.TryGetProperty("version", out var vProp) ? vProp.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrWhiteSpace(latestGuid))
            {
                return;
            }

            string targetVersionDir = _paths.GetVersionDirectory(latestGuid);
            if (!force &&
                string.Equals(existingGuid, latestGuid, StringComparison.OrdinalIgnoreCase) &&
                VersionInstallationValidator.IsVersionInstallationComplete(targetVersionDir, isStudio))
            {
                context.ResolvedVersionGuid = latestGuid;
                return;
            }

            context.ResolvedVersionGuid = latestGuid;
            context.LatestVersion = latestVersion;
            context.CdnBaseUrl = cdnBaseUrl;
            context.Channel = channel;
            context.RequiresDownload = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("VersionCheckStep", $"Update/deploy step fell back to local version: {ex.Message}");
            context.ResolvedVersionGuid = existingGuid;
            context.RequiresDownload = false;
        }
    }
}
