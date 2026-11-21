using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Core.Launch;

public interface IRobloxLauncher
{
    Task<LaunchResult> LaunchPlayerAsync(LaunchSettings settings, CancellationToken ct = default);
    Task<LaunchResult> LaunchAccountAsync(RobloxAccount account, LaunchTarget target, CancellationToken ct = default);
}
