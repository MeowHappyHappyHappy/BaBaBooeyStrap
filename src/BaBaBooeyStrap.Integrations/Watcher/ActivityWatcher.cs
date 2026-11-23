using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.Integrations.Watcher;

public sealed class ActivityWatcher : IActivityWatcher
{
    private const int MaxLineBytes = 65_536;
    private const int MaxHistoryEntries = 100;
    private static readonly TimeSpan DirectoryRescanThrottle = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;
    private readonly ActivityWatcherOptions _options;
    private readonly object _stateLock = new();
    private readonly AsyncLocal<bool> _isInsideWatchLoop = new();
    private readonly List<ActivitySessionData> _history = new();
    private readonly List<byte> _carryBuffer = new();
    private readonly byte[] _readBuffer = new byte[8192];

    private CancellationTokenSource? _cts;
    private Task? _watchTask;
    private FileSystemWatcher? _dirWatcher;
    private string? _activeFilePath;
    private long _activeFileGeneration;
    private DateTime _activeFileCreatedUtc;
    private DateTime _activeFileLastWriteUtc;
    private DateTime _watchStartUtc;
    private DateTime _lastDirScanUtc = DateTime.MinValue;
    private volatile bool _dirRescanRequested = true;
    private long _byteOffset;
    private bool _discardingOverlongLine;
    private bool _isWatching;
    private bool _disposed;

    private ActivitySessionData? _pendingJoin;
    private ActivitySessionData? _currentActivity;
    private bool _inGame;
    private bool _teleportPending;
    private bool _reservedTeleportMarker;
    private DateTime _lastRpcTimeUtc = DateTime.MinValue;

    public event EventHandler<GameJoinEventArgs>? GameJoined;
    public event EventHandler<GameLeaveEventArgs>? GameLeft;
    public event EventHandler<BloxstrapRpcEventArgs>? BloxstrapRpcReceived;
    public event EventHandler? AppClosed;
    public event EventHandler<string>? LogLineRead;
    public event EventHandler<DateTime>? ServerStartTimeParsed;

    public bool IsWatching
    {
        get { lock (_stateLock) return _isWatching && !_disposed; }
    }

    public bool InGame
    {
        get { lock (_stateLock) return _inGame; }
    }

    public string? CurrentLogFilePath
    {
        get { lock (_stateLock) return _activeFilePath; }
    }

    public ActivitySessionData? CurrentActivity
    {
        get { lock (_stateLock) return _currentActivity; }
    }

    public IReadOnlyList<ActivitySessionData> History
    {
        get { lock (_stateLock) return _history.ToList(); }
    }

    public ActivityWatcher(ILogger? logger = null, ActivityWatcherOptions? options = null)
    {
        _logger = logger ?? Logger.Current;
        _options = options ?? new ActivityWatcherOptions();
    }

    public void StartWatching(string logDirectoryOrFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectoryOrFilePath);

        while (true)
        {
            FileSystemWatcher? oldDirWatcher;
            CancellationTokenSource? oldCts;
            Task? oldWatchTask;

            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_dirWatcher == null && _cts == null && _watchTask == null)
                {
                    _byteOffset = 0;
                    _carryBuffer.Clear();
                    _discardingOverlongLine = false;
                    _pendingJoin = null;
                    _currentActivity = null;
                    _inGame = false;
                    _teleportPending = false;
                    _reservedTeleportMarker = false;
                    _lastRpcTimeUtc = DateTime.MinValue;
                    _activeFilePath = null;
                    _activeFileCreatedUtc = DateTime.MinValue;
                    _activeFileLastWriteUtc = DateTime.MinValue;
                    _activeFileGeneration++;
                    _watchStartUtc = DateTime.UtcNow;
                    _lastDirScanUtc = DateTime.MinValue;
                    _dirRescanRequested = true;

                    bool isDirectory = Directory.Exists(logDirectoryOrFilePath) ||
                                       logDirectoryOrFilePath.EndsWith(Path.DirectorySeparatorChar) ||
                                       logDirectoryOrFilePath.EndsWith(Path.AltDirectorySeparatorChar);

                    if (!isDirectory)
                    {
                        _activeFilePath = logDirectoryOrFilePath;
                    }
                    else
                    {
                        SetupDirectoryWatcher(logDirectoryOrFilePath);
                    }

                    _cts = new CancellationTokenSource();
                    _isWatching = true;
                    var token = _cts.Token;
                    _watchTask = Task.Run(() => WatchLoopAsync(logDirectoryOrFilePath, isDirectory, token), token);
                    return;
                }

                DetachWatchResourcesLocked(out oldDirWatcher, out oldCts, out oldWatchTask);
            }

            CleanupDetachedWatchResources(oldDirWatcher, oldCts, oldWatchTask);
        }
    }

    public void StopWatching()
    {
        FileSystemWatcher? dirWatcher;
        CancellationTokenSource? cts;
        Task? watchTask;

        lock (_stateLock)
        {
            DetachWatchResourcesLocked(out dirWatcher, out cts, out watchTask);
        }

        CleanupDetachedWatchResources(dirWatcher, cts, watchTask);
    }

    private void DetachWatchResourcesLocked(
        out FileSystemWatcher? dirWatcher,
        out CancellationTokenSource? cts,
        out Task? watchTask)
    {
        _isWatching = false;

        dirWatcher = _dirWatcher;
        _dirWatcher = null;

        cts = _cts;
        _cts = null;

        watchTask = _watchTask;
        _watchTask = null;
    }

    private void CleanupDetachedWatchResources(
        FileSystemWatcher? dirWatcher,
        CancellationTokenSource? cts,
        Task? watchTask)
    {
        if (dirWatcher != null)
        {
            try
            {
                dirWatcher.EnableRaisingEvents = false;
                dirWatcher.Dispose();
            }
            catch { }
        }

        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            if (watchTask != null && !_isInsideWatchLoop.Value && Task.CurrentId != watchTask.Id)
            {
                try { watchTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            }
            try { cts.Dispose(); } catch { }
        }
    }

    private void SetupDirectoryWatcher(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath)) return;

            _dirWatcher = new FileSystemWatcher(directoryPath, "*.log")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            _dirWatcher.Created += (_, e) =>
            {
                _dirRescanRequested = true;
                TrySelectDirectoryFile(e.FullPath);
            };
            _dirWatcher.Error += (_, e) =>
            {
                _logger.Warn("ActivityWatcher", $"FileSystemWatcher error: {e.GetException()?.Message}");
                _dirRescanRequested = true;
            };
        }
        catch (Exception ex)
        {
            _logger.Debug("ActivityWatcher", $"FileSystemWatcher initialization failed for '{directoryPath}': {ex.Message}");
        }
    }

    private void TrySelectDirectoryFile(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath)) return;
        if (_options.ExcludedLogFilePredicate?.Invoke(candidatePath) == true) return;

        string fileName = Path.GetFileName(candidatePath);
        bool isPlayerLog = fileName.Contains("Player", StringComparison.OrdinalIgnoreCase);

        lock (_stateLock)
        {
            if (_disposed || !_isWatching) return;

            if (_activeFilePath != null && string.Equals(_activeFilePath, candidatePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_options.LockToFirstSelectedLogFile &&
                _activeFilePath != null &&
                (_inGame || Path.GetFileName(_activeFilePath).Contains("Player", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // Prefer *Player*.log over generic *.log if we already have one or if both exist
            if (_activeFilePath != null &&
                Path.GetFileName(_activeFilePath).Contains("Player", StringComparison.OrdinalIgnoreCase) &&
                !isPlayerLog)
            {
                return;
            }

            try
            {
                var info = new FileInfo(candidatePath);
                bool isNewer = _activeFilePath == null ||
                               info.LastWriteTimeUtc > _activeFileLastWriteUtc ||
                               (info.LastWriteTimeUtc == _activeFileLastWriteUtc &&
                                info.CreationTimeUtc >= _activeFileCreatedUtc);
                if (isNewer)
                {
                    _activeFilePath = candidatePath;
                    _activeFileLastWriteUtc = info.LastWriteTimeUtc;
                    _activeFileCreatedUtc = info.CreationTimeUtc;
                    _activeFileGeneration++;
                }
            }
            catch { }
        }
    }

    private (string? FilePath, long Generation) ResolveTargetFile(string path, bool isDirectory)
    {
        if (!isDirectory)
        {
            lock (_stateLock) return (_activeFilePath, _activeFileGeneration);
        }

        if (_options.LockToFirstSelectedLogFile)
        {
            lock (_stateLock)
            {
                if (_activeFilePath != null &&
                    (_inGame || Path.GetFileName(_activeFilePath).Contains("Player", StringComparison.OrdinalIgnoreCase)))
                {
                    return (_activeFilePath, _activeFileGeneration);
                }
            }
        }

        if (!Directory.Exists(path)) return (null, 0);

        DateTime nowUtc = DateTime.UtcNow;
        lock (_stateLock)
        {
            if (_dirWatcher != null &&
                !_dirRescanRequested &&
                _activeFilePath != null &&
                (nowUtc - _lastDirScanUtc) < DirectoryRescanThrottle)
            {
                return (_activeFilePath, _activeFileGeneration);
            }
        }

        try
        {
            _lastDirScanUtc = nowUtc;
            _dirRescanRequested = false;

            var files = Directory.GetFiles(path, "*.log");
            if (_options.ExcludedLogFilePredicate != null)
            {
                files = files.Where(f => !_options.ExcludedLogFilePredicate(f)).ToArray();
            }

            if (files.Length == 0)
            {
                lock (_stateLock) return (_activeFilePath, _activeFileGeneration);
            }

            var playerLogs = files
                .Where(f => Path.GetFileName(f).Contains("Player", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var candidates = playerLogs.Length > 0 ? playerLogs : files;

            FileInfo? newest = null;
            foreach (string file in candidates)
            {
                var fi = new FileInfo(file);
                if (newest == null || CompareFileRecency(fi, newest) > 0)
                {
                    newest = fi;
                }
            }

            if (newest != null)
            {
                bool recentEnough = (DateTime.UtcNow - newest.LastWriteTimeUtc) <= _options.DirectoryScanWindow ||
                                    newest.CreationTimeUtc >= _watchStartUtc.AddSeconds(-2);

                if (recentEnough)
                {
                    lock (_stateLock)
                    {
                        bool isNewerThanActive = _activeFilePath == null ||
                            (!string.Equals(_activeFilePath, newest.FullName, StringComparison.OrdinalIgnoreCase) &&
                             (newest.LastWriteTimeUtc > _activeFileLastWriteUtc ||
                              (newest.LastWriteTimeUtc == _activeFileLastWriteUtc &&
                               newest.CreationTimeUtc > _activeFileCreatedUtc)));

                        if (isNewerThanActive)
                        {
                            _activeFilePath = newest.FullName;
                            _activeFileLastWriteUtc = newest.LastWriteTimeUtc;
                            _activeFileCreatedUtc = newest.CreationTimeUtc;
                            _activeFileGeneration++;
                        }
                    }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        lock (_stateLock) return (_activeFilePath, _activeFileGeneration);
    }

    private async Task WatchLoopAsync(string configuredPath, bool isDirectory, CancellationToken ct)
    {
        _isInsideWatchLoop.Value = true;
        string? lastTargetFile = null;
        long lastGeneration = -1;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var (targetFile, generation) = ResolveTargetFile(configuredPath, isDirectory);
                    if (generation != lastGeneration ||
                        !string.Equals(lastTargetFile, targetFile, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(lastTargetFile) && File.Exists(lastTargetFile))
                        {
                            ReadAvailableLines(lastTargetFile);
                        }

                        if (lastTargetFile != null)
                        {
                            ResetSessionStateForLogSwitch(DateTime.UtcNow);
                        }

                        _byteOffset = 0;
                        _carryBuffer.Clear();
                        _discardingOverlongLine = false;
                        lastTargetFile = targetFile;
                        lastGeneration = generation;
                    }

                    if (!string.IsNullOrEmpty(targetFile) && File.Exists(targetFile))
                    {
                        ReadAvailableLines(targetFile);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Debug("ActivityWatcher", $"Watch loop iteration warning: {ex.Message}");
                }

                try
                {
                    await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            _isInsideWatchLoop.Value = false;
        }
    }

    private void ReadAvailableLines(string filePath)
    {
        List<string>? completedLines = null;

        try
        {
            using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            long fileLength = fs.Length;
            if (fileLength < _byteOffset)
            {
                // Log file truncated or replaced
                _byteOffset = 0;
                _carryBuffer.Clear();
                _discardingOverlongLine = false;
            }

            if (fileLength == _byteOffset)
            {
                return;
            }

            fs.Seek(_byteOffset, SeekOrigin.Begin);
            int bytesRead;
            while ((bytesRead = fs.Read(_readBuffer, 0, _readBuffer.Length)) > 0)
            {
                _byteOffset += bytesRead;
                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = _readBuffer[i];
                    if (b == (byte)'\n')
                    {
                        if (_discardingOverlongLine)
                        {
                            _discardingOverlongLine = false;
                            _carryBuffer.Clear();
                            continue;
                        }

                        int count = _carryBuffer.Count;
                        if (count > 0 && _carryBuffer[count - 1] == (byte)'\r')
                        {
                            count--;
                        }

                        string decoded = count > 0
                            ? Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_carryBuffer)[..count])
                            : string.Empty;

                        if (decoded.Length > 0 && decoded[0] == '\uFEFF')
                        {
                            decoded = decoded[1..];
                        }

                        _carryBuffer.Clear();
                        completedLines ??= new List<string>();
                        completedLines.Add(decoded);
                    }
                    else if (!_discardingOverlongLine)
                    {
                        if (_carryBuffer.Count >= MaxLineBytes)
                        {
                            _discardingOverlongLine = true;
                            _carryBuffer.Clear();
                            _logger.Warn("ActivityWatcher", $"Discarding log line exceeding {MaxLineBytes} bytes.");
                        }
                        else
                        {
                            _carryBuffer.Add(b);
                        }
                    }
                }
            }
        }
        catch (IOException)
        {
            // Transient exclusive lock or file deletion; retry on next poll
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        if (completedLines != null)
        {
            foreach (string line in completedLines)
            {
                try
                {
                    ProcessLine(line);
                }
                catch (Exception ex)
                {
                    _logger.Warn("ActivityWatcher", $"Failed to process log line: {ex.Message}");
                }
            }
        }
    }

    public void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        RaiseSafe(LogLineRead, line);

        DateTime timestamp = RobloxLogLineParser.ParseTimestamp(line);

        // 1. Teleport tracking
        if (RobloxLogLineParser.TryParseTeleport(line, out bool isReserved))
        {
            lock (_stateLock)
            {
                _teleportPending = true;
                if (isReserved)
                {
                    _reservedTeleportMarker = true;
                }
            }
            return;
        }

        // 2. ! Joining game '<jobId>' place <placeId> at <serverIp>
        if (RobloxLogLineParser.TryParseJoinGame(line, out string jobId, out string placeId, out string joinServerIp))
        {
            lock (_stateLock)
            {
                bool allowJoin = !_inGame ||
                                 _teleportPending ||
                                 !string.Equals(_currentActivity?.JobId, jobId, StringComparison.OrdinalIgnoreCase);

                if (allowJoin)
                {
                    bool isTeleport = _inGame || _teleportPending;
                    _pendingJoin = new ActivitySessionData
                    {
                        JobId = jobId,
                        PlaceId = placeId,
                        ServerIp = joinServerIp,
                        IsTeleport = isTeleport,
                        ServerType = _reservedTeleportMarker ? ServerType.Reserved : ServerType.Public
                    };
                    _teleportPending = false;
                    _reservedTeleportMarker = false;
                }
            }
            return;
        }

        // 3. game_join_loadtime / universeid / userid / referral_page
        if (RobloxLogLineParser.TryParseJoinMetadata(line, out string? universeId, out string? userId, out bool isPrivateServer))
        {
            lock (_stateLock)
            {
                var target = _pendingJoin ?? _currentActivity;
                if (target != null)
                {
                    if (universeId != null)
                    {
                        target.UniverseId = universeId;
                    }

                    if (userId != null)
                    {
                        target.UserId = userId;
                    }

                    if (isPrivateServer && target.ServerType != ServerType.Reserved)
                    {
                        target.ServerType = ServerType.Private;
                    }
                }
            }
            return;
        }

        // 4. UDMUX Address = <udmuxIp>, Port = <udmuxPort> | RCC Server Address = <rccIp>, Port = <rccPort>
        if (RobloxLogLineParser.TryParseUdmux(line, out string rccIp, out string udmuxIp, out int? udmuxPort))
        {
            lock (_stateLock)
            {
                if (_pendingJoin != null &&
                    (string.IsNullOrEmpty(rccIp) || string.Equals(rccIp, _pendingJoin.ServerIp, StringComparison.Ordinal)))
                {
                    _pendingJoin.ServerIp = udmuxIp;
                    if (udmuxPort.HasValue)
                    {
                        _pendingJoin.ServerPort = udmuxPort.Value;
                    }
                    _pendingJoin.IsUdmux = true;
                }
            }
            return;
        }

        // 5. serverId: <ip>|<port> -> Emit GameJoined
        if (RobloxLogLineParser.TryParseServerId(line, out string serverIp, out int? serverPort))
        {
            GameLeaveEventArgs? previousLeaveArgs = null;
            GameJoinEventArgs? joinArgs = null;
            lock (_stateLock)
            {
                if (_pendingJoin != null && string.Equals(serverIp, _pendingJoin.ServerIp, StringComparison.Ordinal))
                {
                    if (_pendingJoin.ServerPort == 0 && serverPort.HasValue)
                    {
                        _pendingJoin.ServerPort = serverPort.Value;
                    }

                    if (_inGame && _currentActivity != null)
                    {
                        _currentActivity.TimeLeft = timestamp;
                        AddToHistoryLocked(_currentActivity);
                        previousLeaveArgs = new GameLeaveEventArgs(timestamp);
                    }

                    _pendingJoin.TimeJoined = timestamp;
                    _currentActivity = _pendingJoin;
                    _pendingJoin = null;
                    _inGame = true;

                    joinArgs = new GameJoinEventArgs(
                        _currentActivity.PlaceId,
                        _currentActivity.UniverseId,
                        _currentActivity.JobId,
                        _currentActivity.ServerIp,
                        _currentActivity.ServerPort,
                        _currentActivity.IsUdmux,
                        timestamp);
                }
            }

            if (previousLeaveArgs != null)
            {
                RaiseSafe(GameLeft, previousLeaveArgs);
            }

            if (joinArgs != null)
            {
                RaiseSafe(GameJoined, joinArgs);
            }
            return;
        }

        // 6. Server Prefix (uptime timestamp)
        if (RobloxLogLineParser.TryParseServerPrefixTimestamp(line, out DateTime serverStart))
        {
            lock (_stateLock)
            {
                if (_currentActivity != null)
                {
                    _currentActivity.ServerStartTime = serverStart;
                }
                else if (_pendingJoin != null)
                {
                    _pendingJoin.ServerStartTime = serverStart;
                }
            }
            RaiseSafe(ServerStartTimeParsed, serverStart);
            return;
        }

        // 7. [BloxstrapRPC] JSON
        if (RobloxLogLineParser.TryExtractBloxstrapRpcJson(line, out string rpcJson))
        {
            HandleBloxstrapRpc(rpcJson, timestamp);
            return;
        }

        // 8. Time to disconnect replication data: -> GameLeft
        if (RobloxLogLineParser.IsDisconnectLine(line))
        {
            GameLeaveEventArgs? leaveArgs = null;
            lock (_stateLock)
            {
                if (_inGame && _currentActivity != null)
                {
                    _currentActivity.TimeLeft = timestamp;
                    AddToHistoryLocked(_currentActivity);

                    bool keepPendingTeleport = _pendingJoin != null &&
                        (_pendingJoin.IsTeleport ||
                         !string.Equals(_pendingJoin.JobId, _currentActivity.JobId, StringComparison.OrdinalIgnoreCase));

                    _currentActivity = null;
                    if (!keepPendingTeleport)
                    {
                        _pendingJoin = null;
                    }
                    _inGame = false;
                    leaveArgs = new GameLeaveEventArgs(timestamp);
                }
                else if (_pendingJoin != null && !_pendingJoin.IsTeleport)
                {
                    _pendingJoin = null;
                }
            }

            if (leaveArgs != null)
            {
                RaiseSafe(GameLeft, leaveArgs);
            }
            return;
        }

        // 9. leaveUGCGameInternal -> AppClosed (+ cancel incomplete join)
        if (RobloxLogLineParser.IsAppCloseLine(line))
        {
            lock (_stateLock)
            {
                if (!_inGame && _pendingJoin != null)
                {
                    _pendingJoin = null;
                }
                _teleportPending = false;
                _reservedTeleportMarker = false;
            }
            RaiseSafe(AppClosed, EventArgs.Empty);
        }
    }

    private void AddToHistoryLocked(ActivitySessionData session)
    {
        _history.Insert(0, session);
        if (_history.Count > MaxHistoryEntries)
        {
            _history.RemoveRange(MaxHistoryEntries, _history.Count - MaxHistoryEntries);
        }
    }

    private static int CompareFileRecency(FileInfo a, FileInfo b)
    {
        int cmp = a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);
        if (cmp != 0) return cmp;
        cmp = a.CreationTimeUtc.CompareTo(b.CreationTimeUtc);
        if (cmp != 0) return cmp;
        return StringComparer.OrdinalIgnoreCase.Compare(a.FullName, b.FullName);
    }

    private void ResetSessionStateForLogSwitch(DateTime timestamp)
    {
        GameLeaveEventArgs? leaveArgs = null;
        lock (_stateLock)
        {
            if (_inGame && _currentActivity != null)
            {
                _currentActivity.TimeLeft = timestamp;
                AddToHistoryLocked(_currentActivity);
                leaveArgs = new GameLeaveEventArgs(timestamp);
            }

            _currentActivity = null;
            _pendingJoin = null;
            _inGame = false;
            _teleportPending = false;
            _reservedTeleportMarker = false;
            _lastRpcTimeUtc = DateTime.MinValue;
        }

        if (leaveArgs != null)
        {
            RaiseSafe(GameLeft, leaveArgs);
        }
    }

    private void HandleBloxstrapRpc(string rawJson, DateTime timestamp)
    {
        DateTime nowUtc = DateTime.UtcNow;
        lock (_stateLock)
        {
            if (_options.RpcRateLimit > TimeSpan.Zero && (nowUtc - _lastRpcTimeUtc) < _options.RpcRateLimit)
            {
                return;
            }

            _lastRpcTimeUtc = nowUtc;
        }

        if (!RobloxLogLineParser.TryParseBloxstrapRpcMessage(rawJson, out var parsed, out var parseError))
        {
            if (parseError != null)
            {
                _logger.Debug("ActivityWatcher", $"Ignoring malformed BloxstrapRPC JSON: {parseError}");
            }
            return;
        }

        if (string.Equals(parsed!.Command, "SetLaunchData", StringComparison.OrdinalIgnoreCase))
        {
            if (parsed.Data is not { ValueKind: JsonValueKind.String } dataElem)
            {
                return;
            }

            string? launchData = dataElem.GetString();
            if (launchData == null || launchData.Length > 200)
            {
                return;
            }

            lock (_stateLock)
            {
                if (_currentActivity != null)
                {
                    _currentActivity.RpcLaunchData = launchData;
                }
            }
        }

        RaiseSafe(BloxstrapRpcReceived, new BloxstrapRpcEventArgs(parsed.Command!, rawJson, timestamp));
    }

    private void RaiseSafe(EventHandler? handler, EventArgs args)
    {
        if (handler == null) return;
        foreach (EventHandler subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex)
            {
                _logger.Warn("ActivityWatcher", $"Event subscriber threw an exception: {ex.Message}");
            }
        }
    }

    private void RaiseSafe<T>(EventHandler<T>? handler, T args)
    {
        if (handler == null) return;
        foreach (EventHandler<T> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex)
            {
                _logger.Warn("ActivityWatcher", $"Event subscriber threw an exception: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? dirWatcher;
        CancellationTokenSource? cts;
        Task? watchTask;

        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            DetachWatchResourcesLocked(out dirWatcher, out cts, out watchTask);
        }

        CleanupDetachedWatchResources(dirWatcher, cts, watchTask);
        GC.SuppressFinalize(this);
    }
}
