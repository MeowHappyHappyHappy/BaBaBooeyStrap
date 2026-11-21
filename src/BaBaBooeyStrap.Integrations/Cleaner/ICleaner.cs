namespace BaBaBooeyStrap.Integrations.Cleaner;

public record CleanerPolicy(
    TimeSpan LogAgeThreshold,
    long MaxTotalSizeBytes,
    bool CleanLogs,
    bool CleanCrashDumps,
    bool CleanLauncherLogs,
    bool CleanHttpCache
);

public record CleanerReport(
    int FilesDeleted,
    long BytesFreed,
    IReadOnlyList<string> Errors
);

public interface ICleaner
{
    CleanerReport ExecuteCleanup(CleanerPolicy policy);
}
