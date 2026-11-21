using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.Integrations.Watcher;

public sealed partial class ActivityWatcher : IActivityWatcher
{
    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2}T[\d:.]+Z),", RegexOptions.Compiled)]
    private static partial Regex HeaderTimestampRegex();

    [GeneratedRegex(@"!\s*Joining game '(?<jobId>[0-9a-fA-F\-]{36}|[^']+)'\s+place\s+(?<placeId>\d+)\s+at\s+(?<serverIp>[0-9.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex JoinGameRegex();

    [GeneratedRegex(@"universeid:(?<universeId>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UniverseIdRegex();

    [GeneratedRegex(@"userid:(?<userId>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UserIdRegex();

    [GeneratedRegex(@"referral_page:(?<referral>[^,\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ReferralRegex();

    [GeneratedRegex(@"JoinTypeId(?:%22|"")?(?:%3a|:)(?<joinTypeId>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TeleportJoinTypeRegex();

    [GeneratedRegex(@"UDMUX Address\s*=\s*(?<udmuxIp>[0-9.]+),\s*Port\s*=\s*(?<udmuxPort>\d+)(?:\s*\|\s*RCC Server Address\s*=\s*(?<rccIp>[0-9.]+),\s*Port\s*=\s*(?<rccPort>\d+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UdmuxRegex();

    [GeneratedRegex(@"serverId:\s*(?<serverIp>[0-9.]+)\|(?<serverPort>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ServerIdRegex();

    [GeneratedRegex(@"Server Prefix:.+_(?<timestamp>\d{8}T\d{6}Z)_RCC_[0-9a-zA-Z]+", RegexOptions.Compiled)]
    private static partial Regex ServerPrefixRegex();

    [GeneratedRegex(@"\[BloxstrapRPC\]\s*(?<json>\{.*\})\s*$", RegexOptions.Compiled)]
    private static partial Regex BloxstrapRpcRegex();

    [GeneratedRegex(@"Time to disconnect replication data:(?:\s*(?<val>[\d.]+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DisconnectRegex();

    private readonly ILogger _logger;
    private readonly ActivityWatcherOptions _options;
    private readonly object _stateLock = new();
    private readonly AsyncLocal<bool> _isInsideWatchLoop = new();
    private readonly List<ActivitySessionData> _history = new();
    private readonly List<byte> _carryBuffer = new();

    private CancellationTokenSource? _cts;
    private Task? _watchTask;
    private FileSystemWatcher? _dirWatcher;
    private string? _activeFilePath;
    private long _activeFileGeneration;
    private DateTime _activeFileCreatedUtc;
    private DateTime _watchStartUtc;
    private long _byteOffset;
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
                    _pendingJoin = null;
                    _currentActivity = null;
                    _inGame = false;
                    _teleportPending = false;
                    _reservedTeleportMarker = false;
                    _lastRpcTimeUtc = DateTime.MinValue;
                    _activeFilePath = null;
                    _activeFileCreatedUtc = DateTime.MinValue;
                    _activeFileGeneration++;
                    _watchStartUtc = DateTime.UtcNow;

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

            _dirWatcher.Created += (_, e) => TrySelectDirectoryFile(e.FullPath);
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
                if (_activeFilePath == null || info.CreationTimeUtc >= _activeFileCreatedUtc)
                {
                    _activeFilePath = candidatePath;
                    _activeFileCreatedUtc = info.CreationTimeUtc;
                    _activeFileGeneration++;
                    _pendingJoin = null;
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

        try
        {
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
                if (newest == null || fi.LastWriteTimeUtc > newest.LastWriteTimeUtc || fi.CreationTimeUtc > newest.CreationTimeUtc)
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
                        if (_activeFilePath == null ||
                            (!string.Equals(_activeFilePath, newest.FullName, StringComparison.OrdinalIgnoreCase) &&
                             newest.CreationTimeUtc > _activeFileCreatedUtc))
                        {
                            _activeFilePath = newest.FullName;
                            _activeFileCreatedUtc = newest.CreationTimeUtc;
                            _activeFileGeneration++;
                            _pendingJoin = null;
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
                        _byteOffset = 0;
                        _carryBuffer.Clear();
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
            }

            if (fileLength == _byteOffset)
            {
                return;
            }

            fs.Seek(_byteOffset, SeekOrigin.Begin);
            byte[] readBuf = new byte[8192];
            int bytesRead;
            while ((bytesRead = fs.Read(readBuf, 0, readBuf.Length)) > 0)
            {
                _byteOffset += bytesRead;
                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = readBuf[i];
                    if (b == (byte)'\n')
                    {
                        int count = _carryBuffer.Count;
                        if (count > 0 && _carryBuffer[count - 1] == (byte)'\r')
                        {
                            count--;
                        }

                        string decoded = count > 0
                            ? Encoding.UTF8.GetString(_carryBuffer.ToArray(), 0, count)
                            : string.Empty;

                        if (decoded.Length > 0 && decoded[0] == '\uFEFF')
                        {
                            decoded = decoded[1..];
                        }

                        _carryBuffer.Clear();
                        completedLines ??= new List<string>();
                        completedLines.Add(decoded);
                    }
                    else
                    {
                        _carryBuffer.Add(b);
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
                ProcessLine(line);
            }
        }
    }

    public void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        LogLineRead?.Invoke(this, line);

        DateTime timestamp = ParseLineTimestamp(line);

        // 1. Teleport tracking
        if (line.Contains("doTeleport: joinScriptUrl", StringComparison.OrdinalIgnoreCase))
        {
            _teleportPending = true;
            var joinTypeMatch = TeleportJoinTypeRegex().Match(line);
            if (joinTypeMatch.Success &&
                int.TryParse(joinTypeMatch.Groups["joinTypeId"].Value, out int joinTypeId) &&
                (joinTypeId == 4 || joinTypeId == 6))
            {
                _reservedTeleportMarker = true;
            }
            return;
        }

        // 2. ! Joining game '<jobId>' place <placeId> at <serverIp>
        var joinMatch = JoinGameRegex().Match(line);
        if (joinMatch.Success)
        {
            lock (_stateLock)
            {
                if (!_inGame)
                {
                    _pendingJoin = new ActivitySessionData
                    {
                        JobId = joinMatch.Groups["jobId"].Value,
                        PlaceId = joinMatch.Groups["placeId"].Value,
                        ServerIp = joinMatch.Groups["serverIp"].Value,
                        IsTeleport = _teleportPending,
                        ServerType = _reservedTeleportMarker ? ServerType.Reserved : ServerType.Public
                    };
                    _teleportPending = false;
                    _reservedTeleportMarker = false;
                }
            }
            return;
        }

        // 3. game_join_loadtime / universeid / userid / referral_page
        if (line.Contains("game_join_loadtime", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("universeid:", StringComparison.OrdinalIgnoreCase))
        {
            lock (_stateLock)
            {
                var target = _pendingJoin ?? _currentActivity;
                if (target != null)
                {
                    var univMatch = UniverseIdRegex().Match(line);
                    if (univMatch.Success)
                    {
                        target.UniverseId = univMatch.Groups["universeId"].Value;
                    }

                    var userMatch = UserIdRegex().Match(line);
                    if (userMatch.Success)
                    {
                        target.UserId = userMatch.Groups["userId"].Value;
                    }

                    var refMatch = ReferralRegex().Match(line);
                    if (refMatch.Success && target.ServerType != ServerType.Reserved)
                    {
                        string referral = refMatch.Groups["referral"].Value;
                        if (referral.Contains("RequestPrivateGame", StringComparison.OrdinalIgnoreCase) ||
                            referral.Contains("GameDetailPageJSHybridEvent", StringComparison.OrdinalIgnoreCase))
                        {
                            target.ServerType = ServerType.Private;
                        }
                    }
                }
            }
            return;
        }

        // 4. UDMUX Address = <udmuxIp>, Port = <udmuxPort> | RCC Server Address = <rccIp>, Port = <rccPort>
        var udmuxMatch = UdmuxRegex().Match(line);
        if (udmuxMatch.Success)
        {
            lock (_stateLock)
            {
                if (_pendingJoin != null)
                {
                    string rccIp = udmuxMatch.Groups["rccIp"].Value;
                    if (string.IsNullOrEmpty(rccIp) ||
                        string.Equals(rccIp, _pendingJoin.ServerIp, StringComparison.Ordinal))
                    {
                        _pendingJoin.ServerIp = udmuxMatch.Groups["udmuxIp"].Value;
                        if (int.TryParse(udmuxMatch.Groups["udmuxPort"].Value, out int udmuxPort))
                        {
                            _pendingJoin.ServerPort = udmuxPort;
                        }
                        _pendingJoin.IsUdmux = true;
                    }
                }
            }
            return;
        }

        // 5. serverId: <ip>|<port> -> Emit GameJoined
        var serverMatch = ServerIdRegex().Match(line);
        if (serverMatch.Success)
        {
            GameJoinEventArgs? joinArgs = null;
            lock (_stateLock)
            {
                if (_pendingJoin != null)
                {
                    string serverIp = serverMatch.Groups["serverIp"].Value;
                    if (string.Equals(serverIp, _pendingJoin.ServerIp, StringComparison.Ordinal))
                    {
                        if (_pendingJoin.ServerPort == 0 &&
                            int.TryParse(serverMatch.Groups["serverPort"].Value, out int serverPort))
                        {
                            _pendingJoin.ServerPort = serverPort;
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
            }

            if (joinArgs != null)
            {
                GameJoined?.Invoke(this, joinArgs);
            }
            return;
        }

        // 6. Server Prefix (uptime timestamp)
        var prefixMatch = ServerPrefixRegex().Match(line);
        if (prefixMatch.Success &&
            DateTime.TryParseExact(
                prefixMatch.Groups["timestamp"].Value,
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime serverStart))
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
            ServerStartTimeParsed?.Invoke(this, serverStart);
            return;
        }

        // 7. [BloxstrapRPC] JSON
        var rpcMatch = BloxstrapRpcRegex().Match(line);
        if (rpcMatch.Success)
        {
            HandleBloxstrapRpc(rpcMatch.Groups["json"].Value, timestamp);
            return;
        }

        // 8. Time to disconnect replication data: -> GameLeft
        if (DisconnectRegex().IsMatch(line))
        {
            GameLeaveEventArgs? leaveArgs = null;
            lock (_stateLock)
            {
                if (_inGame && _currentActivity != null)
                {
                    _currentActivity.TimeLeft = timestamp;
                    _history.Insert(0, _currentActivity);
                    _currentActivity = null;
                    _pendingJoin = null;
                    _inGame = false;
                    leaveArgs = new GameLeaveEventArgs(timestamp);
                }
                else if (_pendingJoin != null)
                {
                    _pendingJoin = null;
                }
            }

            if (leaveArgs != null)
            {
                GameLeft?.Invoke(this, leaveArgs);
            }
            return;
        }

        // 9. leaveUGCGameInternal -> AppClosed (+ cancel incomplete join)
        if (line.Contains("leaveUGCGameInternal", StringComparison.OrdinalIgnoreCase))
        {
            lock (_stateLock)
            {
                if (!_inGame && _pendingJoin != null)
                {
                    _pendingJoin = null;
                }
            }
            AppClosed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HandleBloxstrapRpc(string rawJson, DateTime timestamp)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (_options.RpcRateLimit > TimeSpan.Zero && (nowUtc - _lastRpcTimeUtc) < _options.RpcRateLimit)
        {
            return;
        }

        BloxstrapRpcMessage? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<BloxstrapRpcMessage>(rawJson);
        }
        catch (JsonException ex)
        {
            _logger.Debug("ActivityWatcher", $"Ignoring malformed BloxstrapRPC JSON: {ex.Message}");
            return;
        }

        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Command))
        {
            return;
        }

        if (string.Equals(parsed.Command, "SetLaunchData", StringComparison.OrdinalIgnoreCase))
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

        _lastRpcTimeUtc = nowUtc;
        BloxstrapRpcReceived?.Invoke(this, new BloxstrapRpcEventArgs(parsed.Command, rawJson, timestamp));
    }

    private static DateTime ParseLineTimestamp(string line)
    {
        var match = HeaderTimestampRegex().Match(line);
        if (match.Success &&
            DateTime.TryParse(
                match.Groups["ts"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime parsed))
        {
            return parsed;
        }

        return DateTime.UtcNow;
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
