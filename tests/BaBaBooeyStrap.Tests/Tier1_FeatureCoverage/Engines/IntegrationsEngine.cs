using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BaBaBooeyStrap.Core.Interop;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

public record GameJoinEventArgs(long PlaceId, string JobId, string ServerIp, string? UdmuxIp = null, int? UdmuxPort = null);

public interface IActivityWatcher : IDisposable
{
    event EventHandler<GameJoinEventArgs>? GameJoined;
    event EventHandler<EventArgs>? GameLeft;
    event EventHandler<string>? RpcCommandReceived;
    void StartWatching(string logFilePath);
    void StopWatching();
}

public sealed class ActivityWatcher : IActivityWatcher
{
    private static readonly Regex JoinGameRegex = new(
        @"!\s*Joining game '(?<jobId>[^']+)'\s*place\s*(?<placeId>\d+)\s*at\s*(?<serverIp>[0-9.]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UdmuxRegex = new(
        @"UDMUX Address = (?<udmuxIp>[0-9.]+),\s*Port = (?<udmuxPort>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RpcRegex = new(
        @"\[BloxstrapRPC\]\s*(?<json>.*)",
        RegexOptions.Compiled);

    public event EventHandler<GameJoinEventArgs>? GameJoined;
    public event EventHandler<EventArgs>? GameLeft;
    public event EventHandler<string>? RpcCommandReceived;

    private CancellationTokenSource? _cts;
    private Task? _watcherTask;
    private string? _currentLogFile;
    private readonly object _lock = new();

    public void StartWatching(string logFilePath)
    {
        lock (_lock)
        {
            StopWatching();
            _currentLogFile = logFilePath;
            _cts = new CancellationTokenSource();
            _watcherTask = Task.Run(() => WatchLoop(logFilePath, _cts.Token));
        }
    }

    public void StopWatching()
    {
        lock (_lock)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                try { _watcherTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
                _cts.Dispose();
                _cts = null;
                _watcherTask = null;
            }
        }
    }

    private void WatchLoop(string filePath, CancellationToken ct)
    {
        // Wait for file creation if it doesn't exist yet
        var waitSw = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(filePath) && !ct.IsCancellationRequested && waitSw.Elapsed < TimeSpan.FromSeconds(3))
        {
            Thread.Sleep(50);
        }
        if (!File.Exists(filePath) || ct.IsCancellationRequested) return;

        long lastPosition = 0;
        string? cachedUdmuxIp = null;
        int? cachedUdmuxPort = null;
        var pendingBytes = new List<byte>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < lastPosition)
                {
                    // File was truncated/rotated
                    lastPosition = 0;
                    pendingBytes.Clear();
                }

                if (fs.Length > lastPosition)
                {
                    fs.Seek(lastPosition, SeekOrigin.Begin);
                    byte[] buffer = new byte[fs.Length - lastPosition];
                    int totalRead = 0;
                    while (totalRead < buffer.Length)
                    {
                        int read = fs.Read(buffer, totalRead, buffer.Length - totalRead);
                        if (read == 0) break;
                        totalRead += read;
                    }
                    lastPosition += totalRead;

                    for (int i = 0; i < totalRead; i++)
                    {
                        byte b = buffer[i];
                        if (b == (byte)'\n')
                        {
                            if (pendingBytes.Count > 0 && pendingBytes[^1] == (byte)'\r')
                            {
                                pendingBytes.RemoveAt(pendingBytes.Count - 1);
                            }

                            string completeLine = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(pendingBytes));
                            pendingBytes.Clear();
                            ProcessLine(completeLine, ref cachedUdmuxIp, ref cachedUdmuxPort);
                        }
                        else
                        {
                            pendingBytes.Add(b);
                        }
                    }
                }
            }
            catch (IOException)
            {
                // File might be briefly locked
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Keep watcher resilient against transient parsing/filesystem errors
            }

            Thread.Sleep(25);
        }
    }

    private void ProcessLine(string line, ref string? udmuxIp, ref int? udmuxPort)
    {
        var udmuxMatch = UdmuxRegex.Match(line);
        if (udmuxMatch.Success)
        {
            udmuxIp = udmuxMatch.Groups["udmuxIp"].Value;
            if (int.TryParse(udmuxMatch.Groups["udmuxPort"].Value, out int port))
                udmuxPort = port;
        }

        var joinMatch = JoinGameRegex.Match(line);
        if (joinMatch.Success)
        {
            if (long.TryParse(joinMatch.Groups["placeId"].Value, out long placeId))
            {
                string jobId = joinMatch.Groups["jobId"].Value;
                string serverIp = joinMatch.Groups["serverIp"].Value;
                GameJoined?.Invoke(this, new GameJoinEventArgs(placeId, jobId, serverIp, udmuxIp, udmuxPort));
            }
            return;
        }

        var rpcMatch = RpcRegex.Match(line);
        if (rpcMatch.Success)
        {
            RpcCommandReceived?.Invoke(this, rpcMatch.Groups["json"].Value.Trim());
            return;
        }

        if (line.Contains("Leaving game", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Game disconnected", StringComparison.OrdinalIgnoreCase))
        {
            GameLeft?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        StopWatching();
    }
}

public sealed class DiscordRpcClient : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipeClient;
    private readonly object _lock = new();
    private bool _connected;
    private DateTime _lastPresenceUpdateUtc = DateTime.MinValue;

    public bool IsConnected => _connected && _pipeClient?.IsConnected == true;

    public DiscordRpcClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? "discord-ipc-0";
    }

    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_connected && _pipeClient?.IsConnected == true) return true;
        }

        try
        {
            var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            await client.ConnectAsync(cts.Token);

            // Send handshake opcode 0
            byte[] handshakePayload = Encoding.UTF8.GetBytes("{\"v\":1,\"client_id\":\"1234567890\"}");
            await WriteFrameAsync(client, 0, handshakePayload, cts.Token);

            // Read handshake response
            var (respOpcode, respPayload) = await ReadFrameAsync(client, cts.Token);
            if (respOpcode == 1)
            {
                lock (_lock)
                {
                    _pipeClient = client;
                    _connected = true;
                }
                return true;
            }

            client.Dispose();
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task SetActivityAsync(string details, string state, long? placeId = null, bool hideButtons = false, CancellationToken ct = default)
    {
        // Enforce 128 byte limit
        if (details.Length > 128) details = details.Substring(0, 128);
        if (state.Length > 128) state = state.Substring(0, 128);

        // Throttle updates
        DateTime now = DateTime.UtcNow;
        if ((now - _lastPresenceUpdateUtc).TotalMilliseconds < 50)
        {
            // Drop rapid duplicate updates to prevent rate limiting
            return;
        }
        _lastPresenceUpdateUtc = now;

        object activityObj;
        if (placeId.HasValue && !hideButtons)
        {
            activityObj = new
            {
                details,
                state,
                buttons = new[]
                {
                    new { label = "Join Experience", url = $"roblox://experiences/start?placeId={placeId.Value}" }
                }
            };
        }
        else
        {
            activityObj = new { details, state };
        }

        var framePayload = new
        {
            cmd = "SET_ACTIVITY",
            args = new { activity = activityObj }
        };

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(framePayload, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        await SendFrameAsync(1, jsonBytes, ct);
    }

    public async Task ClearPresenceAsync(CancellationToken ct = default)
    {
        var framePayload = new
        {
            cmd = "SET_ACTIVITY",
            args = new { activity = (object?)null }
        };
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(framePayload, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        await SendFrameAsync(1, jsonBytes, ct);
    }

    private async Task SendFrameAsync(int opcode, byte[] payload, CancellationToken ct)
    {
        NamedPipeClientStream? client;
        lock (_lock)
        {
            client = _pipeClient;
        }
        if (client == null || !client.IsConnected) return;

        try
        {
            await WriteFrameAsync(client, opcode, payload, ct);
        }
        catch (IOException)
        {
            lock (_lock)
            {
                _connected = false;
                _pipeClient?.Dispose();
                _pipeClient = null;
            }
        }
    }

    private static async Task WriteFrameAsync(NamedPipeClientStream stream, int opcode, byte[] payload, CancellationToken ct)
    {
        byte[] frame = new byte[8 + payload.Length];
        BitConverter.GetBytes(opcode).CopyTo(frame, 0);
        BitConverter.GetBytes(payload.Length).CopyTo(frame, 4);
        payload.CopyTo(frame, 8);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<(int Opcode, byte[] Payload)> ReadFrameAsync(NamedPipeClientStream stream, CancellationToken ct)
    {
        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header, 0, 8, ct);
        int opcode = BitConverter.ToInt32(header, 0);
        int length = BitConverter.ToInt32(header, 4);
        if (length < 0 || length > 65536)
            throw new InvalidDataException($"Invalid Discord IPC frame length: {length}");

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, 0, length, ct);
        return (opcode, payload);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connected = false;
            _pipeClient?.Dispose();
            _pipeClient = null;
        }
    }
}

public static class WindowManipulator
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    public static bool ApplyTitle(nint hWnd, string title)
    {
        if (hWnd == nint.Zero || hWnd == unchecked((nint)0xDEADBEEF)) return false;
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (!IsWindow(hWnd)) return false;
        return User32.SetWindowTextW(hWnd, title);
    }

    public static bool ApplyIcon(nint hWnd, nint hIcon)
    {
        if (hWnd == nint.Zero || hWnd == unchecked((nint)0xDEADBEEF)) return false;
        if (!IsWindow(hWnd)) return false;
        User32.SendMessageW(hWnd, User32.WM_SETICON, (nint)User32.ICON_SMALL, hIcon);
        User32.SendMessageW(hWnd, User32.WM_SETICON, (nint)User32.ICON_BIG, hIcon);
        return true;
    }

    public static bool SetBorderlessFullscreen(nint hWnd, bool enable, int x = 0, int y = 0, int cx = 1920, int cy = 1080)
    {
        if (hWnd == nint.Zero || hWnd == unchecked((nint)0xDEADBEEF)) return false;
        if (!IsWindow(hWnd)) return false;

        const int WS_CAPTION = 0x00C00000;
        const int WS_THICKFRAME = 0x00040000;
        const int WS_POPUP = unchecked((int)0x80000000);

        nint style = User32.GetWindowLongPtr(hWnd, User32.GWL_STYLE);
        if (enable)
        {
            long newStyle = (style.ToInt64() & ~(WS_CAPTION | WS_THICKFRAME)) | WS_POPUP;
            User32.SetWindowLongPtr(hWnd, User32.GWL_STYLE, (nint)newStyle);
            User32.SetWindowPos(hWnd, nint.Zero, x, y, cx, cy, User32.SWP_FRAMECHANGED | User32.SWP_SHOWWINDOW);
        }
        else
        {
            long newStyle = (style.ToInt64() & ~WS_POPUP) | (WS_CAPTION | WS_THICKFRAME);
            User32.SetWindowLongPtr(hWnd, User32.GWL_STYLE, (nint)newStyle);
            User32.SetWindowPos(hWnd, nint.Zero, x, y, cx, cy, User32.SWP_FRAMECHANGED | User32.SWP_SHOWWINDOW);
        }

        return true;
    }

    public static WindowEventHookSession InstallWinEventHook(
        uint processId,
        Action<uint, nint> onEvent,
        uint eventMin = User32.EVENT_OBJECT_NAMECHANGE,
        uint eventMax = User32.EVENT_OBJECT_NAMECHANGE)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        return new WindowEventHookSession(processId, onEvent, eventMin, eventMax);
    }

    public sealed class WindowEventHookSession : IDisposable
    {
        private readonly User32.WinEventDelegate _callback;
        private nint _hookHandle;
        private bool _disposed;

        public nint Handle => _hookHandle;
        public bool IsDisposed => _disposed;

        public WindowEventHookSession(uint processId, Action<uint, nint> onEvent, uint eventMin, uint eventMax)
        {
            _callback = (_, eventType, hwnd, idObject, idChild, _, _) =>
            {
                if (idObject == User32.OBJID_WINDOW && idChild == User32.CHILDID_SELF)
                {
                    onEvent(eventType, hwnd);
                }
            };

            _hookHandle = User32.SetWinEventHook(
                eventMin,
                eventMax,
                nint.Zero,
                _callback,
                processId,
                0,
                User32.WINEVENT_OUTOFCONTEXT);
        }

        public bool Unhook()
        {
            if (_hookHandle == nint.Zero)
            {
                return false;
            }

            nint handle = _hookHandle;
            _hookHandle = nint.Zero;
            return User32.UnhookWinEvent(handle);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Unhook();
        }
    }
}

public record CleanerResult(int FilesDeleted, long BytesReclaimed);

public static class Cleaner
{
    public static CleanerResult Clean(IEnumerable<string> directoryPaths, TimeSpan retentionAge, string? allowedRootPrefix = null)
    {
        int deleted = 0;
        long bytesReclaimed = 0;
        DateTime cutoff = DateTime.UtcNow - retentionAge;

        foreach (string dir in directoryPaths)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            string fullDir = Path.GetFullPath(dir);
            if (!string.IsNullOrEmpty(allowedRootPrefix))
            {
                if (!ValidateDirectorySafety(fullDir, allowedRootPrefix))
                {
                    string fullAllowed = Path.GetFullPath(allowedRootPrefix);
                    throw new InvalidOperationException($"Safety violation: Path '{fullDir}' is outside allowed prefix '{fullAllowed}'.");
                }
            }

            if (!Directory.Exists(fullDir)) continue;

            var dirInfo = new DirectoryInfo(fullDir);
            foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (file.LastWriteTimeUtc < cutoff)
                    {
                        long size = file.Length;
                        if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
                        {
                            file.Attributes &= ~FileAttributes.ReadOnly;
                        }
                        file.Delete();
                        deleted++;
                        bytesReclaimed += size;
                    }
                }
                catch (IOException)
                {
                    // Locked file -> safely skip
                }
                catch (UnauthorizedAccessException)
                {
                    // Permission denied -> safely skip
                }
            }
        }

        return new CleanerResult(deleted, bytesReclaimed);
    }

    public static bool ValidateDirectorySafety(string dirPath, string safeRoot)
    {
        if (string.IsNullOrWhiteSpace(dirPath) || string.IsNullOrWhiteSpace(safeRoot)) return false;

        string trimmedDir = Path.GetFullPath(dirPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string trimmedSafe = Path.GetFullPath(safeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return trimmedDir.Equals(trimmedSafe, StringComparison.OrdinalIgnoreCase) ||
               trimmedDir.StartsWith(trimmedSafe + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

public record GeoLocation(string Country, string City, string Region)
{
    public static readonly GeoLocation Unknown = new("Unknown", "Unknown", "Unknown");
}

public static class RoValraService
{
    public static async Task<GeoLocation> GetGeoLocationAsync(string ip, HttpClient client, CancellationToken ct = default)
    {
        try
        {
            string url = $"https://ipinfo.io/{ip}/json";
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return GeoLocation.Unknown;

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            string country = doc.RootElement.TryGetProperty("country", out var c) ? c.GetString() ?? "Unknown" : "Unknown";
            string city = doc.RootElement.TryGetProperty("city", out var ci) ? ci.GetString() ?? "Unknown" : "Unknown";
            string region = doc.RootElement.TryGetProperty("region", out var r) ? r.GetString() ?? "Unknown" : "Unknown";
            return new GeoLocation(country, city, region);
        }
        catch
        {
            return GeoLocation.Unknown;
        }
    }

    public static async Task<Dictionary<string, int>> GetRegionLatenciesAsync(string endpoint, HttpClient client, CancellationToken ct = default)
    {
        var latencies = new Dictionary<string, int>();
        try
        {
            using var resp = await client.GetAsync(endpoint, ct);
            if (!resp.IsSuccessStatusCode) return latencies;

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.TryGetInt32(out int latency) && latency >= 0 && latency < 5000)
                {
                    latencies[prop.Name] = latency;
                }
            }
        }
        catch { }
        return latencies;
    }

    public static string FormatLocationBadge(GeoLocation geo)
    {
        string flag = geo.Country.ToUpperInvariant() switch
        {
            "US" => "🇺🇸",
            "GB" => "🇬🇧",
            "DE" => "🇩🇪",
            "JP" => "🇯🇵",
            _ => "🌐"
        };
        return $"{flag} {geo.City}";
    }

    public static int ApplyJitter(int baseScore, bool enableJitter, int maxJitter = 10)
    {
        if (!enableJitter) return baseScore;
        int jitter = Random.Shared.Next(-maxJitter, maxJitter + 1);
        return Math.Max(0, baseScore + jitter);
    }
}
