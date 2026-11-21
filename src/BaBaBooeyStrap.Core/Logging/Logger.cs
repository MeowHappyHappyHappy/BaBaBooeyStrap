using System.Text;

namespace BaBaBooeyStrap.Core.Logging;

public class Logger : ILogger
{
    private static ILogger _current = new Logger();
    public static ILogger Current => _current;
    public static void SetLogger(ILogger logger) => _current = logger ?? throw new ArgumentNullException(nameof(logger));

    public static void InitializeStatic(string? customLogDirectory = null, bool useTempDir = false) =>
        _current.Initialize(customLogDirectory, useTempDir);
    public static void LogStatic(LogLevel level, string identifier, string message) =>
        _current.Log(level, identifier, message);
    public static void DebugStatic(string identifier, string message) =>
        _current.Debug(identifier, message);
    public static void InfoStatic(string identifier, string message) =>
        _current.Info(identifier, message);
    public static void WarnStatic(string identifier, string message) =>
        _current.Warn(identifier, message);
    public static void ErrorStatic(string identifier, string message) =>
        _current.Error(identifier, message);
    public static void ExceptionStatic(string identifier, Exception ex) =>
        _current.Exception(identifier, ex);

    private readonly object _syncLock = new();
    private readonly List<string> _history = new();
    private FileStream? _fileStream;
    private StreamWriter? _writer;
    private bool _disposed;

    public LogLevel MinLevel { get; set; } = LogLevel.Info;
    public bool Initialized { get; private set; }
    public bool NoWriteMode { get; set; }
    public string? FileLocation { get; private set; }
    public IReadOnlyList<string> History
    {
        get
        {
            lock (_syncLock)
            {
                return _history.ToList();
            }
        }
    }

    public Logger() { }

    public void Initialize(string? customLogDirectory = null, bool useTempDir = false)
    {
        lock (_syncLock)
        {
            if (Initialized) return;

            string logDir;
            if (!string.IsNullOrWhiteSpace(customLogDirectory))
            {
                logDir = customLogDirectory;
            }
            else if (useTempDir)
            {
                logDir = Paths.TempLogs;
            }
            else
            {
                logDir = Paths.Logs;
            }

            try
            {
                Directory.CreateDirectory(logDir);
                CleanOldLogs(logDir, 7);

                if (!NoWriteMode)
                {
                    string timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
                    int pid = Environment.ProcessId;
                    string filename = $"BaBaBooeyStrap_{timestamp}_{pid}.log";
                    FileLocation = Path.Combine(logDir, filename);

                    _fileStream = new FileStream(FileLocation, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(_fileStream, Encoding.UTF8) { AutoFlush = true };
                }

                Initialized = true;
            }
            catch (Exception)
            {
                // Fall back to memory-only logging if filesystem access fails
                NoWriteMode = true;
                Initialized = true;
            }
        }
    }

    public void Log(LogLevel level, string identifier, string message)
    {
        if (level < MinLevel) return;

        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        int pid = Environment.ProcessId;
        int tid = Environment.CurrentManagedThreadId;
        string levelStr = level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO ",
            LogLevel.Warn => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "INFO "
        };

        string sanitizedMessage = SanitizeMessage(message);
        string formattedLine = $"[{timestamp}] [{pid}:{tid}] [{levelStr}] [{identifier}] {sanitizedMessage}";

        lock (_syncLock)
        {
            _history.Add(formattedLine);

            if (_writer != null && !NoWriteMode)
            {
                try
                {
                    _writer.WriteLine(formattedLine);
                }
                catch { }
            }
        }
    }

    public void Debug(string identifier, string message) => Log(LogLevel.Debug, identifier, message);
    public void Info(string identifier, string message) => Log(LogLevel.Info, identifier, message);
    public void Warn(string identifier, string message) => Log(LogLevel.Warn, identifier, message);
    public void Error(string identifier, string message) => Log(LogLevel.Error, identifier, message);

    public void Exception(string identifier, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        Log(LogLevel.Error, identifier, $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    }

    public void Flush()
    {
        lock (_syncLock)
        {
            try
            {
                _writer?.Flush();
                _fileStream?.Flush();
            }
            catch { }
        }
    }

    private static string SanitizeMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        string userProfile = Paths.UserProfile;
        if (!string.IsNullOrEmpty(userProfile) && message.Contains(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            message = message.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        return message;
    }

    private static void CleanOldLogs(string directory, int retentionDays)
    {
        try
        {
            var dirInfo = new DirectoryInfo(directory);
            if (!dirInfo.Exists) return;

            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            foreach (var file in dirInfo.GetFiles("*.log"))
            {
                if (file.LastWriteTimeUtc <= cutoff)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch
                    {
                        // File might be open by another process, ignore
                    }
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _writer?.Flush();
                _writer?.Dispose();
                _fileStream?.Dispose();
            }
            catch { }

            _writer = null;
            _fileStream = null;
        }
        GC.SuppressFinalize(this);
    }
}
