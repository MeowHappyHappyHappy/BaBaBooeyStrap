using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.Integrations.Cleaner;

public static class CleanerPolicyMapper
{
    public const long DefaultMaxTotalSizeBytes = 500L * 1024L * 1024L;

    public static TimeSpan ToAgeThreshold(CleanerOptions option) => option switch
    {
        CleanerOptions.OneDay => TimeSpan.FromDays(1),
        CleanerOptions.OneWeek => TimeSpan.FromDays(7),
        CleanerOptions.TwoWeeks => TimeSpan.FromDays(14),
        CleanerOptions.OneMonth => TimeSpan.FromDays(30),
        CleanerOptions.TwoMonths => TimeSpan.FromDays(60),
        _ => TimeSpan.Zero
    };

    public static CleanerPolicy FromSettings(Settings settings, long maxTotalSizeBytes = DefaultMaxTotalSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.CleanerOptions == CleanerOptions.Never)
        {
            return new CleanerPolicy(TimeSpan.Zero, 0, false, false, false, false);
        }

        var dirs = settings.CleanerDirectories ?? [];
        bool cleanLogs = dirs.Contains("RobloxLogs", StringComparer.OrdinalIgnoreCase);
        bool cleanCrashDumps = dirs.Contains("RobloxCrashDumps", StringComparer.OrdinalIgnoreCase);
        bool cleanLauncherLogs = dirs.Contains("BaBaBooeyStrapLogs", StringComparer.OrdinalIgnoreCase);
        bool cleanHttpCache = dirs.Contains("RobloxCache", StringComparer.OrdinalIgnoreCase);

        return new CleanerPolicy(
            ToAgeThreshold(settings.CleanerOptions),
            maxTotalSizeBytes,
            cleanLogs,
            cleanCrashDumps,
            cleanLauncherLogs,
            cleanHttpCache);
    }
}
