using System.Text;
using System.Text.RegularExpressions;

namespace BaBaBooeyStrap.Core.Logging;

public partial class Logger : ILogger
{
    private const int MaxHistoryEntries = 2000;
    private const long MaxLogFileBytes = 10 * 1024 * 1024;

    private static volatile ILogger _current = new Logger();
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
    private readonly Queue<string> _history = new();
    private FileStream? _fileStream;
    private StreamWriter? _writer;
    private string? _logDirectory;
    private long _currentFileBytes;
    private int _rotationIndex;
    private volatile bool _disposed;

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
            if (_disposed || Initialized) return;

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
                _logDirectory = logDir;
                CleanOldLogs(logDir, 7);

                if (!NoWriteMode)
                {
                    OpenNewLogFileLocked(logDir);
                }

                Initialized = true;
            }
            catch (Exception)
            {
                // Fall back to memory-only logging if filesystem access fails
                try { _writer?.Dispose(); } catch { }
                try { _fileStream?.Dispose(); } catch { }
                _writer = null;
                _fileStream = null;
                NoWriteMode = true;
                Initialized = true;
            }
        }
    }

    private void OpenNewLogFileLocked(string logDir)
    {
        try { _writer?.Flush(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _fileStream?.Dispose(); } catch { }
        _writer = null;
        _fileStream = null;

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        int pid = Environment.ProcessId;
        string suffix = _rotationIndex == 0 ? string.Empty : $"_{_rotationIndex}";
        string filename = $"BaBaBooeyStrap_{timestamp}_{pid}{suffix}.log";
        string path = Path.Combine(logDir, filename);

        FileStream? fs = null;
        StreamWriter? writer = null;
        try
        {
            fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
            FileLocation = path;
            _fileStream = fs;
            _writer = writer;
            _currentFileBytes = fs.Length;
        }
        catch
        {
            try { writer?.Dispose(); } catch { }
            try { fs?.Dispose(); } catch { }
            throw;
        }
    }

    public void Log(LogLevel level, string identifier, string message)
    {
        if (level < MinLevel || _disposed) return;

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
            if (_disposed) return;

            if (_history.Count >= MaxHistoryEntries)
            {
                _history.Dequeue();
            }
            _history.Enqueue(formattedLine);

            if (_writer != null && !NoWriteMode)
            {
                try
                {
                    if (_currentFileBytes >= MaxLogFileBytes && !string.IsNullOrWhiteSpace(_logDirectory))
                    {
                        _rotationIndex++;
                        OpenNewLogFileLocked(_logDirectory);
                    }

                    _writer.WriteLine(formattedLine);
                    _currentFileBytes += Encoding.UTF8.GetByteCount(formattedLine) + Environment.NewLine.Length;
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
        Log(LogLevel.Error, identifier, ex.ToString());
    }

    public void Flush()
    {
        lock (_syncLock)
        {
            if (_disposed) return;
            try
            {
                _writer?.Flush();
                _fileStream?.Flush();
            }
            catch { }
        }
    }

    public static string RedactSensitiveText(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;

        string userProfile = Paths.UserProfile;
        if (!string.IsNullOrEmpty(userProfile) && message.Contains(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            message = message.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        message = RobloSecurityWarningRegex().Replace(message, "[REDACTED_COOKIE]");
        message = RobloSecurityPairRegex().Replace(message, ".ROBLOSECURITY=[REDACTED]");
        message = GameInfoTicketRegex().Replace(message, "gameinfo:[REDACTED]");
        message = AuthTicketHeaderRegex().Replace(message, "$1=[REDACTED]");
        message = SensitiveHttpHeaderRegex().Replace(message, "$1: [REDACTED]");
        message = PrivateServerCodeRegex().Replace(message, "$1=[REDACTED]");

        return message;
    }

    private static string SanitizeMessage(string message) => RedactSensitiveText(message);

    [GeneratedRegex(@"_\|WARNING:-DO-NOT-SHARE-THIS[^\s""'&;+`]*", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex RobloSecurityWarningRegex();

    [GeneratedRegex(@"\.ROBLOSECURITY=[^\s;""'&+`]+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex RobloSecurityPairRegex();

    [GeneratedRegex(@"gameinfo:[^+\s""']+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex GameInfoTicketRegex();

    [GeneratedRegex(@"\b(rbx-authentication-ticket)\s*[:=]\s*[^\s""'&;]+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex AuthTicketHeaderRegex();

    [GeneratedRegex(@"^\s*(Cookie|Authorization|X-CSRF-TOKEN)\s*:\s*[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Multiline, matchTimeoutMilliseconds: 100)]
    private static partial Regex SensitiveHttpHeaderRegex();

    [GeneratedRegex(@"\b(privateServerLinkCode|accessCode|linkCode)=([^&\s""'+`#]+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex PrivateServerCodeRegex();

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
