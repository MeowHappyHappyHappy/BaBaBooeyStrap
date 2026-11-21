using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.Integrations.Discord;

public sealed class DiscordRpcService : IDiscordRpcService
{
    public const int MaxFrameLength = 65536;
    public const uint OpcodeHandshake = 0;
    public const uint OpcodeFrame = 1;
    public const uint OpcodeClose = 2;
    public const uint OpcodePing = 3;
    public const uint OpcodePong = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ClearJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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

                    // Send Opcode 0 Handshake
                    string handshakeJson = JsonSerializer.Serialize(new
                    {
                        v = 1,
                        client_id = _options.ClientId
                    }, JsonOptions);

                    await WriteFrameAsync(candidate, OpcodeHandshake, handshakeJson, connectCts.Token).ConfigureAwait(false);

                    // Read Opcode 1 READY response
                    var (respOpcode, respJson) = await ReadFrameAsync(candidate, connectCts.Token).ConfigureAwait(false);
                    if (respOpcode == OpcodeFrame && IsReadyDispatch(respJson))
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

        var payload = new
        {
            cmd = "SET_ACTIVITY",
            args = new
            {
                pid = Environment.ProcessId,
                activity = (object?)null
            },
            nonce = Guid.NewGuid().ToString()
        };

        string json = JsonSerializer.Serialize(payload, ClearJsonOptions);
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
                string closeJson = JsonSerializer.Serialize(new { code = 1000, message = "Normal closure" }, JsonOptions);
                if (_writeLock.Wait(200))
                {
                    try
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                        WriteFrameAsync(pipeToClose, OpcodeClose, closeJson, closeCts.Token).GetAwaiter().GetResult();
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
        string? launchData = null)
    {
        if (string.IsNullOrWhiteSpace(placeId)) return string.Empty;

        var sb = new StringBuilder($"roblox://experiences/start?placeId={Uri.EscapeDataString(placeId)}");
        if (!string.IsNullOrWhiteSpace(accessCode))
        {
            sb.Append($"&accessCode={Uri.EscapeDataString(accessCode)}");
        }
        else if (!string.IsNullOrWhiteSpace(jobId))
        {
            sb.Append($"&gameInstanceId={Uri.EscapeDataString(jobId)}");
        }

        if (!string.IsNullOrWhiteSpace(launchData))
        {
            string trimmed = launchData.Length > 200 ? launchData[..200] : launchData;
            sb.Append($"&launchData={Uri.EscapeDataString(trimmed)}");
        }

        return sb.ToString();
    }

    public static string FormatPresenceText(string? text, bool padSingleChar = false)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        string result = text.Length > 128 ? text[..128] : text;
        if (Encoding.UTF8.GetByteCount(result) > 128)
        {
            // Trim safely at rune boundary to stay <= 128 UTF-8 bytes
            var sb = new StringBuilder();
            int byteCount = 0;
            foreach (Rune rune in result.EnumerateRunes())
            {
                int runeBytes = rune.Utf8SequenceLength;
                if (byteCount + runeBytes > 128) break;
                sb.Append(rune.ToString());
                byteCount += runeBytes;
            }
            result = sb.ToString();
        }

        if (padSingleChar && result.Length == 1)
        {
            result += "\u2800\u2800";
        }

        return result;
    }

    public string BuildSetActivityJson(DiscordActivity activity)
    {
        string details = FormatPresenceText(activity.Details, padSingleChar: true);
        string state = FormatPresenceText(activity.State, padSingleChar: true);

        object? timestamps = activity.StartTimestamps.HasValue
            ? new { start = activity.StartTimestamps.Value }
            : null;

        string? largeImage = string.IsNullOrWhiteSpace(activity.LargeImageKey) ? null : TruncateUtf8(activity.LargeImageKey, 512);
        string? largeText = string.IsNullOrWhiteSpace(activity.LargeImageText) ? null : FormatPresenceText(activity.LargeImageText, padSingleChar: true);
        string? smallImage = string.IsNullOrWhiteSpace(activity.SmallImageKey) ? null : TruncateUtf8(activity.SmallImageKey, 512);
        string? smallText = string.IsNullOrWhiteSpace(activity.SmallImageText) ? null : FormatPresenceText(activity.SmallImageText, padSingleChar: true);

        bool hasAssets = largeImage != null || largeText != null || smallImage != null || smallText != null;

        object? assets = hasAssets
            ? new
            {
                large_image = largeImage,
                large_text = largeText,
                small_image = smallImage,
                small_text = smallText
            }
            : null;

        List<object>? buttons = null;
        if (!_options.HideButtons && !string.IsNullOrWhiteSpace(activity.DeepLinkUrl))
        {
            string safeDeepLink = TruncateUtf8(activity.DeepLinkUrl, 2048);
            if (!string.IsNullOrEmpty(safeDeepLink))
            {
                buttons = new List<object>
                {
                    new { label = "Join server", url = safeDeepLink }
                };

                if (!string.IsNullOrWhiteSpace(_options.GamePageUrl))
                {
                    string safeGamePageUrl = TruncateUtf8(_options.GamePageUrl, 2048);
                    if (!string.IsNullOrEmpty(safeGamePageUrl))
                    {
                        buttons.Add(new { label = "See game page", url = safeGamePageUrl });
                    }
                }
            }
        }

        // Discord enforces mutual exclusion between buttons and secrets
        object? secrets = (buttons == null || buttons.Count == 0) && !string.IsNullOrWhiteSpace(activity.JoinSecret)
            ? new { join = TruncateUtf8(activity.JoinSecret, 512) }
            : null;

        string SerializeEnvelope(object? currentAssets, List<object>? currentButtons, object? currentSecrets)
        {
            var activityBody = new
            {
                details = string.IsNullOrEmpty(details) ? null : details,
                state = string.IsNullOrEmpty(state) ? null : state,
                status_display_type = (int)_options.StatusDisplayType,
                timestamps,
                assets = currentAssets,
                buttons = currentButtons,
                secrets = currentSecrets
            };

            var envelope = new
            {
                cmd = "SET_ACTIVITY",
                args = new
                {
                    pid = Environment.ProcessId,
                    activity = activityBody
                },
                nonce = Guid.NewGuid().ToString()
            };

            return JsonSerializer.Serialize(envelope, JsonOptions);
        }

        string json = SerializeEnvelope(assets, buttons, secrets);
        if (Encoding.UTF8.GetByteCount(json) > MaxFrameLength)
        {
            json = SerializeEnvelope(assets, null, null);
        }
        if (Encoding.UTF8.GetByteCount(json) > MaxFrameLength)
        {
            json = SerializeEnvelope(null, null, null);
        }
        return json;
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var sb = new StringBuilder();
        int byteCount = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > maxBytes) break;
            sb.Append(rune.ToString());
            byteCount += runeBytes;
        }
        return sb.ToString();
    }

    public static async Task WriteFrameAsync(Stream stream, uint opcode, string json, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxFrameLength)
        {
            throw new InvalidDataException($"Discord IPC frame exceeds maximum size ({payload.Length} > {MaxFrameLength}).");
        }

        byte[] frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 8);

        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<(uint Opcode, string Json)> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header.AsMemory(0, 8), ct).ConfigureAwait(false);

        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

        if (length < 0 || length > MaxFrameLength)
        {
            throw new InvalidDataException($"Invalid Discord IPC frame length: {length}");
        }

        if (length == 0)
        {
            return (opcode, string.Empty);
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload.AsMemory(0, length), ct).ConfigureAwait(false);
        return (opcode, Encoding.UTF8.GetString(payload));
    }

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

    private static bool IsReadyDispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("evt", out var evtProp)) return false;
            if (evtProp.ValueKind != JsonValueKind.String) return false;
            return string.Equals(evtProp.GetString(), "READY", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
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

            await WriteFrameAsync(pipe, opcode, json, ct).ConfigureAwait(false);
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
                var (opcode, json) = await ReadFrameAsync(pipe, ct).ConfigureAwait(false);

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
