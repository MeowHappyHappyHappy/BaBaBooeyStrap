using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Integrations.Cleaner;

namespace BaBaBooeyStrap.App.Services.Pipeline;

public sealed class PreLaunchCleanupStep : IBootstrapPipelineStep
{
    private readonly IPaths _paths;
    private readonly ILogger _logger;

    public string StepName => "PreLaunchCleanup";

    public PreLaunchCleanupStep(IPaths paths, ILogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task ExecuteAsync(BootstrapPipelineContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var appSettings = context.AppSettings;
        if (appSettings.CleanerOptions != CleanerOptions.Never)
        {
            try
            {
                var cleaner = new CacheCleaner(_paths, _logger);
                cleaner.ExecuteCleanup(CleanerPolicyMapper.FromSettings(appSettings));
            }
            catch (Exception ex)
            {
                _logger.Warn("PreLaunchCleanupStep", $"Cache cleanup skipped: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }
}
