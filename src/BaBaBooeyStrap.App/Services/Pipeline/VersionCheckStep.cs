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
        if (!force && PathProvider.TryValidateVersionGuid(existingGuid, out string safeExistingGuid))
        {
            existingGuid = safeExistingGuid;
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
        else if (!string.IsNullOrWhiteSpace(existingGuid))
        {
            _logger.Warn("VersionCheckStep", $"Ignoring invalid persisted version GUID '{existingGuid}'.");
            existingGuid = string.Empty;
            context.ExistingVersionGuid = string.Empty;
            context.ResolvedVersionGuid = string.Empty;
        }

        if (launchSettings.BypassUpdateCheck)
        {
            return;
        }

        try
        {
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
            using var checkTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkTimeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await _httpClient.GetAsync(clientVersionUrl, HttpCompletionOption.ResponseHeadersRead, checkTimeoutCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                if (string.IsNullOrWhiteSpace(existingGuid))
                {
                    context.FailureReason = $"Version check returned HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}).";
                }
                return;
            }

            string json = await resp.Content.ReadAsStringAsync(checkTimeoutCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("clientVersionUpload", out var guidProp) ||
                guidProp.ValueKind != JsonValueKind.String)
            {
                if (string.IsNullOrWhiteSpace(existingGuid))
                {
                    context.FailureReason = "Version check response did not contain 'clientVersionUpload'.";
                }
                return;
            }

            string rawLatestGuid = guidProp.GetString() ?? string.Empty;
            string latestVersion = doc.RootElement.TryGetProperty("version", out var vProp) && vProp.ValueKind == JsonValueKind.String
                ? vProp.GetString() ?? string.Empty
                : string.Empty;

            if (!PathProvider.TryValidateVersionGuid(rawLatestGuid, out string latestGuid))
            {
                _logger.Warn("VersionCheckStep", $"Rejected unsafe remote version GUID '{rawLatestGuid}'.");
                if (string.IsNullOrWhiteSpace(existingGuid))
                {
                    context.FailureReason = $"Invalid remote version GUID '{rawLatestGuid}'.";
                }
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

            context.ReportStatus(LocalizationService.Instance.GetString("Str_Bootstrapper_StatusFindingCdn"));
            var selector = _mirrorSelector ?? new CdnMirrorSelector(_httpClient);
            string cdnBaseUrl = await selector.ResolveOptimalCdnUrlAsync(ct).ConfigureAwait(false);

            context.ResolvedVersionGuid = latestGuid;
            context.LatestVersion = latestVersion;
            context.CdnBaseUrl = cdnBaseUrl;
            context.Channel = channel;
            context.RequiresDownload = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("VersionCheckStep", $"Update/deploy step fell back to local version: {ex.Message}");
            context.ResolvedVersionGuid = existingGuid;
            context.RequiresDownload = false;
            if (string.IsNullOrWhiteSpace(existingGuid))
            {
                context.FailureReason = $"Failed to check Roblox version: {ex.Message}";
            }
        }
    }
}
