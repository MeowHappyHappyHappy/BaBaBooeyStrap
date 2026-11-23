using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Integrations.Custom;

public interface ICustomIntegrationRunner : IDisposable
{
    Task<IReadOnlyList<int>> LaunchIntegrationsAsync(
        IEnumerable<CustomIntegration> integrations,
        bool preLaunchOnly,
        int? trackedRobloxPid = null,
        CancellationToken ct = default);

    void BindPreLaunchedToPid(IReadOnlyList<int> integrationPids, int robloxPid);
    int CloseIntegrationsForPid(int robloxPid);
    int CloseIntegrationPids(IEnumerable<int> integrationPids) => 0;
    int CloseAllAutoCloseIntegrations();
}
