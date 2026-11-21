using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace BaBaBooeyStrap.Tests.Mocks;

/// <summary>
/// Simulates the Discord Desktop Client IPC Named Pipe server (\\.\pipe\discord-ipc-{pipeIndex})
/// with full Opcode 0..4 framing, ReadExactlyAsync safety, and disconnect/fault injection hooks.
/// </summary>
public sealed class MockDiscordIpcServer : IDisposable
{
    public const int MaxFrameLength = 65536;

    public string PipeName { get; }
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _receivedJsonMessages = new();
    private readonly List<(int Opcode, string Json)> _receivedFrames = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private NamedPipeServerStream? _activePipe;
    private Task? _serverTask;
    private int _connectionCount;
    private bool _disposed;

    public IReadOnlyList<string> ReceivedMessages
    {
        get { lock (_lock) return _receivedJsonMessages.ToList(); }
    }

    public IReadOnlyList<(int Opcode, string Json)> ReceivedFrames
    {
        get { lock (_lock) return _receivedFrames.ToList(); }
    }

    public int ConnectionCount
    {
        get { lock (_lock) return _connectionCount; }
    }

    public MockDiscordIpcServer(int pipeIndex = 0) : this($"discord-ipc-{pipeIndex}")
    {
    }

    public MockDiscordIpcServer(string pipeName)
    {
        PipeName = pipeName;
    }

    public void Start()
    {
        _serverTask = Task.Run(RunServerAsync);
    }

    private async Task RunServerAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipeServer = null;
            try
            {
                pipeServer = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipeServer.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                if (_cts.IsCancellationRequested)
                {
                    pipeServer.Dispose();
                    break;
                }

                lock (_lock)
                {
                    _activePipe = pipeServer;
                    _connectionCount++;
                }

                byte[] header = new byte[8];
                while (pipeServer.IsConnected && !_cts.IsCancellationRequested)
                {
                    await pipeServer.ReadExactlyAsync(header.AsMemory(0, 8), _cts.Token).ConfigureAwait(false);

                    int opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

                    if (length < 0 || length > MaxFrameLength)
                    {
                        throw new InvalidDataException($"Invalid Discord IPC frame length: {length}");
                    }

                    string json = string.Empty;
                    if (length > 0)
                    {
                        byte[] payload = new byte[length];
                        await pipeServer.ReadExactlyAsync(payload.AsMemory(0, length), _cts.Token).ConfigureAwait(false);
                        json = Encoding.UTF8.GetString(payload);
                    }

                    lock (_lock)
                    {
                        _receivedJsonMessages.Add(json);
                        _receivedFrames.Add((opcode, json));
                    }

                    if (opcode == 0) // Handshake
                    {
                        const string readyJson = "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\",\"data\":{\"v\":1}}";
                        await WriteFrameToPipeAsync(pipeServer, 1, readyJson, _cts.Token).ConfigureAwait(false);
                    }
                    else if (opcode == 2) // Close
                    {
                        break;
                    }
                    else if (opcode == 3) // Ping -> Pong
                    {
                        string pongBody = string.IsNullOrEmpty(json) ? "{}" : json;
                        await WriteFrameToPipeAsync(pipeServer, 4, pongBody, _cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (EndOfStreamException)
            {
                // Client closed pipe cleanly
            }
            catch (IOException)
            {
                // Pipe broken or simulated disconnect; loop for next connection
            }
            catch (ObjectDisposedException)
            {
                // Simulated disconnect disposed pipe
            }
            catch
            {
                if (_cts.IsCancellationRequested) break;
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_activePipe, pipeServer))
                    {
                        _activePipe = null;
                    }
                }
                pipeServer?.Dispose();
            }
        }
    }

    public async Task SendPingAsync(string payload = "{}")
    {
        NamedPipeServerStream? pipe;
        lock (_lock) pipe = _activePipe;
        if (pipe is { IsConnected: true })
        {
            await WriteFrameToPipeAsync(pipe, 3, payload, _cts.Token).ConfigureAwait(false);
        }
    }

    public async Task SendCloseAndDisconnectAsync(int code = 1000, string message = "Server closing")
    {
        NamedPipeServerStream? pipe;
        lock (_lock) pipe = _activePipe;
        if (pipe is { IsConnected: true })
        {
            string closeJson = JsonSerializer.Serialize(new { code, message });
            try
            {
                await WriteFrameToPipeAsync(pipe, 2, closeJson, _cts.Token).ConfigureAwait(false);
            }
            catch { }
        }
        SimulateDisconnect();
    }

    public async Task SendMalformedOversizedHeaderAsync(int fakeLength = 100_000)
    {
        NamedPipeServerStream? pipe;
        lock (_lock) pipe = _activePipe;
        if (pipe is { IsConnected: true })
        {
            byte[] header = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), fakeLength);
            await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                await pipe.WriteAsync(header, _cts.Token).ConfigureAwait(false);
                await pipe.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }

    public void SimulateDisconnect()
    {
        NamedPipeServerStream? pipe;
        lock (_lock)
        {
            pipe = _activePipe;
            _activePipe = null;
        }
        try { pipe?.Disconnect(); } catch { }
        try { pipe?.Dispose(); } catch { }
    }

    private async Task WriteFrameToPipeAsync(NamedPipeServerStream pipe, int opcode, string json, CancellationToken ct)
    {
        byte[] respBytes = Encoding.UTF8.GetBytes(json);
        byte[] respFrame = new byte[8 + respBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(respFrame.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(respFrame.AsSpan(4, 4), respBytes.Length);
        respBytes.CopyTo(respFrame, 8);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(respFrame, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> WaitForMessageAsync(Func<string, bool> predicate, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            lock (_lock)
            {
                if (_receivedJsonMessages.Any(predicate)) return true;
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<bool> WaitForFrameAsync(Func<(int Opcode, string Json), bool> predicate, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            lock (_lock)
            {
                if (_receivedFrames.Any(predicate)) return true;
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<bool> WaitForConnectionCountAsync(int minCount, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            lock (_lock)
            {
                if (_connectionCount >= minCount) return true;
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
        return false;
    }

    public void ClearMessages()
    {
        lock (_lock)
        {
            _receivedJsonMessages.Clear();
            _receivedFrames.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        SimulateDisconnect();

        try
        {
            // Connect dummy client to unblock WaitForConnectionAsync if pending
            using var dummy = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            dummy.Connect(50);
        }
        catch { }

        try
        {
            _serverTask?.Wait(500);
        }
        catch { }

        _writeLock.Dispose();
        _cts.Dispose();
    }
}
