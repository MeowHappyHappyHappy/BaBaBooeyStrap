using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BaBaBooeyStrap.App.Services;

public sealed class SingleInstanceIpc : IDisposable
{
    public const string DefaultMutexName = @"Local\BaBaBooeyStrap_AppInstanceMutex";
    public const string DefaultPipeName = "BaBaBooeyStrap_AppPipe";

    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;
    private bool _disposed;

    public bool IsPrimaryInstance => _ownsMutex;
    public event EventHandler<string[]>? ArgumentsReceived;

    public SingleInstanceIpc(string? mutexName = null, string? pipeName = null)
    {
        _mutexName = string.IsNullOrWhiteSpace(mutexName) ? DefaultMutexName : mutexName;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
    }

    public bool TryAcquirePrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mutex != null) return _ownsMutex;

        _mutex = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                _ownsMutex = _mutex.WaitOne(0, false);
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
            }
        }
        else
        {
            _ownsMutex = true;
        }

        if (!_ownsMutex)
        {
            _mutex.Dispose();
            _mutex = null;
        }

        return _ownsMutex;
    }

    public void StartPipeServer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_serverTask != null) return;

        _serverCts = new CancellationTokenSource();
        var token = _serverCts.Token;
        _serverTask = Task.Run(() => RunServerLoopAsync(token), token);
    }

    public async Task<bool> SendArgumentsToPrimaryAsync(string[] args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(3);
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(effectiveTimeout);

            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            string payload = JsonSerializer.Serialize(args ?? Array.Empty<string>());
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await client.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
            await client.FlushAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunServerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var ms = new MemoryStream();
                await server.CopyToAsync(ms, ct).ConfigureAwait(false);
                string json = Encoding.UTF8.GetString(ms.ToArray());

                if (!string.IsNullOrWhiteSpace(json))
                {
                    var args = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
                    ArgumentsReceived?.Invoke(this, args);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Transient pipe read failure; continue listening
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _serverCts?.Cancel(); } catch { }
        try { _serverTask?.Wait(250); } catch { }
        try { _serverCts?.Dispose(); } catch { }

        if (_mutex != null)
        {
            if (_ownsMutex)
            {
                try { _mutex.ReleaseMutex(); } catch { }
            }
            _mutex.Dispose();
            _mutex = null;
            _ownsMutex = false;
        }

        GC.SuppressFinalize(this);
    }
}
