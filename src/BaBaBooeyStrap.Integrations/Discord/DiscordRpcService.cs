using System.IO.Pipes;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.Integrations.Discord;

public sealed class DiscordRpcService : IDiscordRpcService
{
    public const int MaxFrameLength = DiscordIpcProtocol.MaxFrameLength;
    public const uint OpcodeHandshake = DiscordIpcProtocol.OpcodeHandshake;
    public const uint OpcodeFrame = DiscordIpcProtocol.OpcodeFrame;
    public const uint OpcodeClose = DiscordIpcProtocol.OpcodeClose;
    public const uint OpcodePing = DiscordIpcProtocol.OpcodePing;
    public const uint OpcodePong = DiscordIpcProtocol.OpcodePong;

    private readonly DiscordRpcOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly List<TimeSpan> _reconnectDelaysObserved = new();

    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _reconnectCts;
    private Task? _readerTask;
    private Task? _reconnectTask;
    private DiscordActivity? _lastActivity;
    private DateTime _lastPresenceUpdateUtc = DateTime.MinValue;
    private bool _isConnected;
    private bool _explicitDisconnect;
    private bool _disposed;

    public event EventHandler<bool>? ConnectionStateChanged;

    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return _isConnected && _pipe is { IsConnected: true } && !_disposed;
            }
        }
    }

    public IReadOnlyList<TimeSpan> ReconnectDelaysObserved
    {
        get
        {
            lock (_stateLock)
            {
                return _reconnectDelaysObserved.ToList();
            }
        }
    }

    public DiscordRpcService(DiscordRpcOptions? options = null, ILogger? logger = null)
    {
        _options = options ?? new DiscordRpcOptions();
        _logger = logger ?? Logger.Current;
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            if (_disposed || IsConnected) return IsConnected;

            lock (_stateLock)
            {
                _explicitDisconnect = false;
            }

            string[] candidatePipes = GetCandidatePipeNames();
            TimeSpan timeoutPerPipe = !string.IsNullOrWhiteSpace(_options.PipeNameOverride)
                ? _options.DirectPipeConnectTimeout
                : _options.PerPipeConnectTimeout;

            foreach (string pipeName in candidatePipes)
            {
                if (ct.IsCancellationRequested || _disposed) break;

                NamedPipeClientStream? candidate = null;
                try
                {
                    candidate = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectCts.CancelAfter(timeoutPerPipe);

                    await candidate.ConnectAsync(connectCts.Token).ConfigureAwait(false);

                    string handshakeJson = DiscordIpcProtocol.BuildHandshakeJson(_options.ClientId);
                    await DiscordIpcProtocol.WriteFrameAsync(candidate, OpcodeHandshake, handshakeJson, connectCts.Token).ConfigureAwait(false);

                    var (respOpcode, respJson) = await DiscordIpcProtocol.ReadFrameAsync(candidate, connectCts.Token).ConfigureAwait(false);
                    if (respOpcode == OpcodeFrame && DiscordIpcProtocol.IsReadyDispatch(respJson))
                    {
                        ActivatePipe(candidate);
                        candidate = null;
                        return true;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Per-pipe timeout; try next slot
                }
                catch (ObjectDisposedException)
                {
                    // Linked token source or service disposed during shutdown
                    return false;
                }
                catch (IOException)
                {
                    // Pipe unavailable or broken; try next slot
                }
                catch (UnauthorizedAccessException)
                {
                    // Pipe permission denied; try next slot
                }
                catch (InvalidDataException ex)
                {
                    _logger.Warn("DiscordRpcService", $"Malformed handshake response on {pipeName}: {ex.Message}");
                }
                finally
                {
                    candidate?.Dispose();
                }
            }

            return false;
        }
        finally
        {
            try { _connectLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public async Task UpdatePresenceAsync(DiscordActivity activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            _lastActivity = activity;

            if (_options.UpdateThrottle > TimeSpan.Zero)
            {
                DateTime now = DateTime.UtcNow;
                if ((now - _lastPresenceUpdateUtc) < _options.UpdateThrottle)
                {
                    return;
                }
                _lastPresenceUpdateUtc = now;
            }
        }

        string json = BuildSetActivityJson(activity);
        await SendFrameSafeAsync(OpcodeFrame, json, ct).ConfigureAwait(false);
    }

    public async Task ClearPresenceAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            _lastActivity = null;
        }

        string json = DiscordIpcProtocol.BuildClearActivityJson(Environment.ProcessId);
        await SendFrameSafeAsync(OpcodeFrame, json, ct).ConfigureAwait(false);
    }

    public async Task SendPingAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SendFrameSafeAsync(OpcodePing, "{}", ct).ConfigureAwait(false);
    }

    public void Disconnect()
    {
        CancellationTokenSource? reconnectCts;
        NamedPipeClientStream? pipeToClose;
        CancellationTokenSource? sessionCts;
        bool wasConnected;

        lock (_stateLock)
        {
            _explicitDisconnect = true;
            wasConnected = _isConnected;
            _isConnected = false;
            pipeToClose = _pipe;
            _pipe = null;
            sessionCts = _sessionCts;
            _sessionCts = null;
            reconnectCts = _reconnectCts;
            _reconnectCts = null;
        }

        try { reconnectCts?.Cancel(); } catch { }
        try { reconnectCts?.Dispose(); } catch { }

        if (pipeToClose is { IsConnected: true })
        {
            try
            {
                string closeJson = DiscordIpcProtocol.BuildCloseJson();
                if (_writeLock.Wait(200))
                {
                    try
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                        DiscordIpcProtocol.WriteFrameAsync(pipeToClose, OpcodeClose, closeJson, closeCts.Token).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        _writeLock.Release();
                    }
                }
            }
            catch { }
        }

        try { sessionCts?.Cancel(); } catch { }
        try { pipeToClose?.Dispose(); } catch { }
        try { sessionCts?.Dispose(); } catch { }

        if (wasConnected)
        {
            ConnectionStateChanged?.Invoke(this, false);
        }
    }

    public static string BuildRobloxDeepLink(
        string placeId,
        string? jobId = null,
        string? accessCode = null,
        string? launchData = null) =>
        DiscordIpcProtocol.BuildRobloxDeepLink(placeId, jobId, accessCode, launchData);

    public static string FormatPresenceText(string? text, bool padSingleChar = false) =>
        DiscordIpcProtocol.FormatPresenceText(text, padSingleChar);

    public string BuildSetActivityJson(DiscordActivity activity) =>
        DiscordIpcProtocol.BuildSetActivityJson(activity, _options, Environment.ProcessId);

    public static Task WriteFrameAsync(Stream stream, uint opcode, string json, CancellationToken ct) =>
        DiscordIpcProtocol.WriteFrameAsync(stream, opcode, json, ct);

    public static Task<(uint Opcode, string Json)> ReadFrameAsync(Stream stream, CancellationToken ct) =>
        DiscordIpcProtocol.ReadFrameAsync(stream, ct);

    private string[] GetCandidatePipeNames()
    {
        if (!string.IsNullOrWhiteSpace(_options.PipeNameOverride))
        {
            return new[] { _options.PipeNameOverride };
        }

        var list = new List<string>();
        for (int i = _options.PipeScanStart; i <= _options.PipeScanEnd; i++)
        {
            list.Add($"{_options.PipeNamePrefix}-{i}");
        }
        return list.ToArray();
    }

    private void ActivatePipe(NamedPipeClientStream pipe)
    {
        CancellationTokenSource sessionCts;
        lock (_stateLock)
        {
            _pipe?.Dispose();
            _pipe = pipe;
            _isConnected = true;
            try { _sessionCts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _sessionCts?.Dispose(); } catch (ObjectDisposedException) { }
            _sessionCts = new CancellationTokenSource();
            sessionCts = _sessionCts;
            _readerTask = Task.Run(() => RunReaderLoopAsync(pipe, sessionCts.Token), sessionCts.Token);
        }

        ConnectionStateChanged?.Invoke(this, true);
    }

    private async Task SendFrameSafeAsync(uint opcode, string json, CancellationToken ct)
    {
        NamedPipeClientStream? pipe;
        lock (_stateLock)
        {
            pipe = _pipe;
        }

        if (pipe == null || !pipe.IsConnected)
        {
            HandleUnexpectedDisconnect();
            return;
        }

        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            lock (_stateLock)
            {
                pipe = _pipe;
            }

            if (pipe == null || !pipe.IsConnected)
            {
                HandleUnexpectedDisconnect();
                return;
            }

            await DiscordIpcProtocol.WriteFrameAsync(pipe, opcode, json, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            _logger.Warn("DiscordRpcService", $"Dropping invalid outbound Discord IPC frame: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.Debug("DiscordRpcService", $"Pipe write failed ({ex.GetType().Name}); marking disconnected.");
            HandleUnexpectedDisconnect();
        }
        finally
        {
            try { _writeLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task RunReaderLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var (opcode, json) = await DiscordIpcProtocol.ReadFrameAsync(pipe, ct).ConfigureAwait(false);

                if (opcode == OpcodePing)
                {
                    await SendFrameSafeAsync(OpcodePong, string.IsNullOrEmpty(json) ? "{}" : json, ct).ConfigureAwait(false);
                }
                else if (opcode == OpcodeClose)
                {
                    _logger.Debug("DiscordRpcService", $"Received CLOSE frame from Discord IPC: {json}");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException or InvalidDataException)
        {
            _logger.Debug("DiscordRpcService", $"Discord IPC reader loop ended: {ex.Message}");
        }

        if (!ct.IsCancellationRequested)
        {
            HandleUnexpectedDisconnect();
        }
    }

    private void HandleUnexpectedDisconnect()
    {
        bool shouldReconnect;
        bool wasConnected;
        NamedPipeClientStream? pipeToDispose;
        CancellationTokenSource? sessionCts;

        lock (_stateLock)
        {
            wasConnected = _isConnected;
            _isConnected = false;
            pipeToDispose = _pipe;
            _pipe = null;
            sessionCts = _sessionCts;
            _sessionCts = null;

            shouldReconnect = _options.AutoReconnect && !_explicitDisconnect && !_disposed &&
                              (_reconnectTask == null || _reconnectTask.IsCompleted);
            if (shouldReconnect)
            {
                try { _reconnectCts?.Cancel(); } catch (ObjectDisposedException) { }
                try { _reconnectCts?.Dispose(); } catch (ObjectDisposedException) { }
                _reconnectCts = new CancellationTokenSource();
                var token = _reconnectCts.Token;
                _reconnectTask = Task.Run(() => RunReconnectLoopAsync(token), token);
            }
        }

        try { sessionCts?.Cancel(); } catch { }
        try { pipeToDispose?.Dispose(); } catch { }
        try { sessionCts?.Dispose(); } catch { }

        if (wasConnected)
        {
            ConnectionStateChanged?.Invoke(this, false);
        }
    }

    private async Task RunReconnectLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = _options.InitialReconnectDelay;
        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromMilliseconds(50);

        while (!ct.IsCancellationRequested && !_explicitDisconnect && !_disposed)
        {
            lock (_stateLock)
            {
                _reconnectDelaysObserved.Add(delay);
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ct.IsCancellationRequested || _explicitDisconnect || _disposed) break;

            bool reconnected;
            try
            {
                reconnected = await ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (reconnected)
            {
                DiscordActivity? cached;
                lock (_stateLock)
                {
                    cached = _lastActivity;
                    _lastPresenceUpdateUtc = DateTime.MinValue;
                }

                if (cached != null)
                {
                    await UpdatePresenceAsync(cached, ct).ConfigureAwait(false);
                }

                lock (_stateLock)
                {
                    if (_isConnected && !_disposed)
                    {
                        _reconnectTask = null;
                        break;
                    }
                }
            }

            long nextMs = Math.Min((long)(delay.TotalMilliseconds * 2), (long)_options.MaxReconnectDelay.TotalMilliseconds);
            delay = TimeSpan.FromMilliseconds(Math.Max(1, nextMs));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();

        try { _readerTask?.Wait(250); } catch { }
        try { _reconnectTask?.Wait(250); } catch { }

        _writeLock.Dispose();
        _connectLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
