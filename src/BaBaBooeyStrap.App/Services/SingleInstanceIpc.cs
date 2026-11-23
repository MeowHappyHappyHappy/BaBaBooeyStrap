using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace BaBaBooeyStrap.App.Services;

public sealed class SingleInstanceIpc : IDisposable
{
    public const string DefaultMutexName = @"Local\BaBaBooeyStrap_AppInstanceMutex";
    public const string DefaultPipeName = "BaBaBooeyStrap_AppPipe";

    internal const int MaxPayloadBytes = 64 * 1024;
    internal const int MaxArgCount = 64;
    internal const int MaxArgLength = 8192;
    private static readonly TimeSpan PerConnectionReadTimeout = TimeSpan.FromSeconds(2);

    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;
    private int _disposed;

    public bool IsPrimaryInstance => _ownsMutex;
    public string PipeName => _pipeName;
    internal string? LastIpcError { get; private set; }
    public event EventHandler<string[]>? ArgumentsReceived;

    public SingleInstanceIpc(string? mutexName = null, string? pipeName = null)
    {
        _mutexName = string.IsNullOrWhiteSpace(mutexName) ? BuildScopedDefaultMutexName() : mutexName;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? BuildScopedDefaultPipeName() : pipeName;
    }

    private static string BuildScopedDefaultMutexName()
    {
        return $@"Local\BaBaBooeyStrap_AppInstanceMutex_{GetCurrentUserScopeSuffix()}";
    }

    internal static string BuildScopedDefaultPipeName()
    {
        return $"{DefaultPipeName}_{GetCurrentUserScopeSuffix()}";
    }

    private static string GetCurrentUserScopeSuffix()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string sid = identity.User?.Value ?? Environment.UserName;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sid));
            string sidPrefix = Convert.ToHexString(hash)[..12];
            int sessionId = Process.GetCurrentProcess().SessionId;
            return $"{sidPrefix}_{sessionId}";
        }
        catch
        {
            return $"s{Environment.ProcessId}";
        }
    }

    public bool TryAcquirePrimary()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_serverTask != null) return;

        _serverCts = new CancellationTokenSource();
        var token = _serverCts.Token;
        NamedPipeServerStream initialServer = CreateServerStream();
        _serverTask = Task.Run(() => RunServerLoopAsync(initialServer, token), token);
    }

    public async Task<bool> SendArgumentsToPrimaryAsync(string[] args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(3);
        try
        {
            string[] sanitizedArgs = ValidateAndSanitizeArgs(args);
            string payload = JsonSerializer.Serialize(sanitizedArgs);
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            if (bytes.Length > MaxPayloadBytes)
            {
                return false;
            }

            bool isLowIl = IsCurrentProcessLowIntegrity();
            using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                isLowIl ? PipeOptions.Asynchronous : (PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly),
                TokenImpersonationLevel.Anonymous);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(effectiveTimeout);

            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            await client.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
            await client.FlushAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            LastIpcError = $"{ex.GetType().Name}: {ex.Message}";
            Core.Logging.Logger.DebugStatic("SingleInstanceIpc", $"SendArgumentsToPrimaryAsync failed: {LastIpcError}");
            return false;
        }
    }

    private const int TokenIntegrityLevel = 25;
    private const uint SecurityMandatoryMediumRid = 0x2000;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint TokenHandle,
        int TokenInformationClass,
        nint TokenInformation,
        uint TokenInformationLength,
        out uint ReturnLength);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint pSid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint pSid, uint nSubAuthority);

    private static bool IsCurrentProcessLowIntegrity()
    {
        nint buffer = nint.Zero;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            nint token = identity.Token;
            if (token == nint.Zero) return false;

            GetTokenInformation(token, TokenIntegrityLevel, nint.Zero, 0, out uint needed);
            if (needed == 0) return false;

            buffer = Marshal.AllocHGlobal((int)needed);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
            {
                return false;
            }

            nint sidPtr = Marshal.ReadIntPtr(buffer, 0);
            if (sidPtr == nint.Zero) return false;

            nint countPtr = GetSidSubAuthorityCount(sidPtr);
            if (countPtr == nint.Zero) return false;

            byte count = Marshal.ReadByte(countPtr);
            if (count == 0) return false;

            nint ridPtr = GetSidSubAuthority(sidPtr, (uint)(count - 1));
            if (ridPtr == nint.Zero) return false;

            uint rid = unchecked((uint)Marshal.ReadInt32(ridPtr));
            return rid < SecurityMandatoryMediumRid;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private NamedPipeServerStream CreateServerStream()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            bool lowIl = IsCurrentProcessLowIntegrity();
            if (lowIl)
            {
                // Low-IL tokens hit NO_WRITE_UP (Access Denied) against Medium-IL explicit ACLs unless inherited from creator.
                return new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }

            if (identity.User is { } userSid)
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(
                    userSid,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
                return NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    security);
            }
        }
        catch (Exception ex)
        {
            Core.Logging.Logger.DebugStatic("SingleInstanceIpc", $"CreateServerStream ACL creation failed, falling back: {ex.Message}");
        }

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private async Task RunServerLoopAsync(NamedPipeServerStream? initialServer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
        {
            NamedPipeServerStream? server = initialServer;
            initialServer = null;
            try
            {
                server ??= CreateServerStream();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(PerConnectionReadTimeout);

                string? json = await ReadBoundedPayloadAsync(server, readCts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var rawArgs = JsonSerializer.Deserialize<string[]>(json);
                    if (rawArgs != null)
                    {
                        string[] validated = ValidateAndSanitizeArgs(rawArgs);
                        ArgumentsReceived?.Invoke(this, validated);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Transient pipe read, timeout, or oversized payload; continue listening
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private static async Task<string?> ReadBoundedPayloadAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[4096];
        int totalRead = 0;

        while (true)
        {
            int read = await server.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;

            totalRead += read;
            if (totalRead > MaxPayloadBytes)
            {
                throw new InvalidDataException($"IPC payload exceeded {MaxPayloadBytes} bytes.");
            }

            ms.Write(buffer, 0, read);
        }

        return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    internal static string[] ValidateAndSanitizeArgs(string[]? args)
    {
        if (args == null || args.Length == 0)
        {
            return Array.Empty<string>();
        }

        int count = Math.Min(args.Length, MaxArgCount);
        var list = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            string arg = args[i] ?? string.Empty;
            if (arg.Length > MaxArgLength)
            {
                arg = arg[..MaxArgLength];
            }
            list.Add(arg);
        }
        return list.ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

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
