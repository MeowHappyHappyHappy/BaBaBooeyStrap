using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BaBaBooeyStrap.Tests.Mocks;

/// <summary>
/// Provides isolated Win32 named mutex and event handles scoped with unique GUIDs,
/// ensuring multi-instance concurrency tests never interfere with running Roblox processes
/// or other tests running in parallel.
/// </summary>
public sealed class ParameterizedMutexTestHelper : IDisposable
{
    public const uint WaitObject0 = 0u;
    public const uint WaitAbandoned = 128u;
    public const uint WaitTimeout = 258u;
    public const uint WaitFailed = uint.MaxValue;

    public string MutexName { get; }
    public string EventName { get; }

    private readonly List<nint> _handles = new();
    private readonly List<ExternalHoldWorker> _workers = new();
    private readonly object _lock = new();
    private bool _disposed;

    public ParameterizedMutexTestHelper(string? prefix = null)
    {
        string id = Guid.NewGuid().ToString("N");
        string tag = prefix ?? "Test";
        // Explicitly use Local\ session namespace to avoid Global\ UAC elevation collisions
        MutexName = $@"Local\{tag}_ROBLOX_Mutex_{id}";
        EventName = $@"Local\{tag}_ROBLOX_Event_{id}";
    }

    /// <summary>
    /// Attempts to acquire the mutex on the calling thread.
    /// Returns an IMutexHoldScope if acquired, or null if timed out or failed.
    /// </summary>
    public IMutexHoldScope? TryAcquire(TimeSpan timeout)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            nint handle = CreateMutexW(nint.Zero, bInitialOwner: false, MutexName);
            if (handle == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            uint waitMs = (uint)timeout.TotalMilliseconds;
            uint res = WaitForSingleObject(handle, waitMs);
            if (res == WaitObject0 || res == WaitAbandoned)
            {
                var scope = new LocalMutexHoldScope(handle);
                return scope;
            }

            CloseHandle(handle);
            return null;
        }
    }

    /// <summary>
    /// Simulates another running process holding the named mutex in the background.
    /// </summary>
    public IMutexHoldScope HoldMutexExternally()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var worker = new ExternalHoldWorker(MutexName);
            worker.Start();
            _workers.Add(worker);
            return worker;
        }
    }

    /// <summary>
    /// Simulates a process acquiring the mutex and then terminating abruptly without releasing it,
    /// creating an abandoned mutex scenario for WAIT_ABANDONED testing.
    /// </summary>
    public void SimulateAbandonedMutex()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var threadReady = new ManualResetEventSlim(false);
            nint workerHandle = nint.Zero;

            var thread = new Thread(() =>
            {
                workerHandle = CreateMutexW(nint.Zero, bInitialOwner: true, MutexName);
                threadReady.Set();
                // Abruptly terminate thread while still holding mutex ownership
            })
            {
                IsBackground = true
            };
            thread.Start();

            if (!threadReady.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("SimulateAbandonedMutex timed out waiting for worker thread.");
            }
            thread.Join(TimeSpan.FromSeconds(2));

            if (workerHandle != nint.Zero)
            {
                _handles.Add(workerHandle);
            }
        }
    }

    /// <summary>
    /// Checks whether the mutex is currently held by any thread/process in the system.
    /// </summary>
    public bool IsMutexHeld(TimeSpan? timeout = null)
    {
        uint waitMs = (uint)(timeout?.TotalMilliseconds ?? 0);
        nint handle = CreateMutexW(nint.Zero, bInitialOwner: false, MutexName);
        if (handle == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            uint result = WaitForSingleObject(handle, waitMs);
            if (result == WaitObject0 || result == WaitAbandoned)
            {
                // We were able to acquire it, so it was NOT held
                ReleaseMutex(handle);
                return false;
            }
            if (result == WaitTimeout)
            {
                // Timed out waiting -> it is currently held
                return true;
            }
            throw new InvalidOperationException($"WaitForSingleObject returned unexpected result {result}.");
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Creates a Win32 named event associated with this test instance.
    /// </summary>
    public nint CreateNamedEvent(bool manualReset = true, bool initialState = false)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            nint handle = CreateEventW(nint.Zero, manualReset, initialState, EventName);
            if (handle == nint.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _handles.Add(handle);
            return handle;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var worker in _workers)
            {
                try { worker.Dispose(); } catch { }
            }
            _workers.Clear();

            foreach (nint handle in _handles)
            {
                if (handle != nint.Zero)
                {
                    try { ReleaseMutex(handle); } catch { }
                    try { CloseHandle(handle); } catch { }
                }
            }
            _handles.Clear();
        }
    }

    private sealed class LocalMutexHoldScope : IMutexHoldScope
    {
        private readonly nint _handle;
        private bool _disposed;

        public bool IsHolding => !_disposed;

        public LocalMutexHoldScope(nint handle)
        {
            _handle = handle;
        }

        public void Release() => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { ReleaseMutex(_handle); } catch { }
            try { CloseHandle(_handle); } catch { }
        }
    }

    private sealed class ExternalHoldWorker : IMutexHoldScope
    {
        private readonly string _mutexName;
        private readonly ManualResetEventSlim _started = new(false);
        private readonly ManualResetEventSlim _stop = new(false);
        private Thread? _thread;
        private bool _disposed;

        public bool IsHolding { get; private set; }

        public ExternalHoldWorker(string mutexName)
        {
            _mutexName = mutexName;
        }

        public void Start()
        {
            _thread = new Thread(Run)
            {
                Name = $"ExternalMutexHolder_{_mutexName}",
                IsBackground = true
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            if (!_started.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException($"Failed to acquire initial hold on mutex {_mutexName} within timeout.");
            }
        }

        private void Run()
        {
            nint handle = CreateMutexW(nint.Zero, bInitialOwner: true, _mutexName);
            if (handle == nint.Zero)
            {
                _started.Set();
                return;
            }

            try
            {
                IsHolding = true;
                _started.Set();
                _stop.Wait();
            }
            finally
            {
                IsHolding = false;
                try { ReleaseMutex(handle); } catch { }
                try { CloseHandle(handle); } catch { }
            }
        }

        public void Release() => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Set();
            if (_thread != null && _thread.IsAlive)
            {
                _thread.Join(TimeSpan.FromSeconds(2));
            }
            _started.Dispose();
            _stop.Dispose();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateMutexW(nint lpMutexAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner, string lpName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateEventW(nint lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseMutex(nint hMutex);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetEvent(nint hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ResetEvent(nint hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint hObject);
}

public interface IMutexHoldScope : IDisposable
{
    bool IsHolding { get; }
    void Release();
}
