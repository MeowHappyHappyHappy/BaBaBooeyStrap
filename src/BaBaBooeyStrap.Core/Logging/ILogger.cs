namespace BaBaBooeyStrap.Core.Logging;

public interface ILogger : IDisposable
{
    LogLevel MinLevel { get; set; }
    bool Initialized { get; }
    bool NoWriteMode { get; }
    string? FileLocation { get; }
    IReadOnlyList<string> History { get; }

    void Initialize(string? customLogDirectory = null, bool useTempDir = false);
    void Log(LogLevel level, string identifier, string message);
    void Debug(string identifier, string message);
    void Info(string identifier, string message);
    void Warn(string identifier, string message);
    void Error(string identifier, string message);
    void Exception(string identifier, Exception ex);
    void Flush();
}
