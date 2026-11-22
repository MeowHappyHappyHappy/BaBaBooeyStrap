using System.Diagnostics;
using System.Runtime.InteropServices;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.MultiInstance.Abstractions;

namespace BaBaBooeyStrap.MultiInstance;

/// <summary>
/// Preemptively holds <c>ROBLOX_singletonMutex</c> and <c>ROBLOX_singletonEvent</c> on a dedicated STA
/// background thread so multiple RobloxPlayerBeta client instances can run concurrently without tripping Byfron anti-cheat.
/// </summary>
public sealed class WindowsMultiInstanceController : IMultiInstanceController, IDisposable
{
    public const string DefaultMutexName = "ROBLOX_singletonMutex";
    public const string DefaultEventName = "ROBLOX_singletonEvent";

    private const int MutexPollMs = 100;
    private const int HealIntervalMs = 2000;

    private readonly string _mutexName;
    private readonly string _eventName;
    private readonly Func<IReadOnlyList<int>>? _processEnumerator;
    private readonly object _stateLock = new();
    private readonly HashSet<int> _manualTrackedPids = new();

    private Thread? _workerThread;
    private ManualResetEventSlim? _stopEvent;
    private ManualResetEventSlim? _initialAttemptDone;

    private nint _mutexHandle;
    private nint _eventHandle;

    private volatile bool _enabled;
    private volatile bool _ownsMutex;
    private volatile bool _waitingForMutex;
    private volatile bool _threadHoldsMutex;
    private volatile string? _lastError;
    private bool _disposed;

    /// <summary>
    /// Initializes a controller using the default Roblox kernel object names.
    /// </summary>
    public WindowsMultiInstanceController()
        : this(DefaultMutexName, DefaultEventName)
    {
    }

    /// <summary>
    /// Initializes a controller with custom kernel object names (primarily for isolated test concurrency)
    /// and an optional process enumerator delegate.
    /// Safe fallback is applied if names are null, empty, or whitespace.
    /// </summary>
    public WindowsMultiInstanceController(string? mutexName, string? eventName = null, Func<IReadOnlyList<int>>? processEnumerator = null)
    {
        _mutexName = string.IsNullOrWhiteSpace(mutexName) ? DefaultMutexName : mutexName;
        _eventName = string.IsNullOrWhiteSpace(eventName) ? DefaultEventName : eventName;
        _processEnumerator = processEnumerator;
    }

    /// <summary>
    /// Starts the dedicated STA background thread and preemptively acquires the mutex and event.
    /// </summary>
    public MultiInstanceStatus Enable()
    {
        ManualResetEventSlim? initialAttempt = null;

        while (true)
        {
            Thread? priorThread = null;

            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_enabled && _workerThread is { IsAlive: true })
                {
                    initialAttempt = _initialAttemptDone;
                    if (initialAttempt == null || initialAttempt.IsSet)
                    {
                        return GetStatusLocked();
                    }
                    break;
                }

                // If a prior worker thread is still running (e.g. terminating from a concurrent/recent Disable),
                // ensure it stops and wait for it to exit before spawning a new worker thread.
                if (_workerThread is { IsAlive: true })
                {
                    _stopEvent?.Set();
                    priorThread = _workerThread;
                }
                else
                {
                    _stopEvent?.Dispose();
                    _initialAttemptDone?.Dispose();

                    _stopEvent = new ManualResetEventSlim(false);
                    _initialAttemptDone = new ManualResetEventSlim(false);
                    initialAttempt = _initialAttemptDone;

                    _enabled = true;
                    _ownsMutex = false;
                    _waitingForMutex = false;
                    _threadHoldsMutex = false;
                    _lastError = null;

                    _workerThread = new Thread(HoldUntilReleased)
                    {
                        Name = "multi-roblox-singleton",
                        IsBackground = true
                    };
                    _workerThread.SetApartmentState(ApartmentState.STA);
                    _workerThread.Start();
                    break;
                }
            }

            if (priorThread is { IsAlive: true } && Thread.CurrentThread != priorThread)
            {
                priorThread.Join(TimeSpan.FromSeconds(5));
            }
        }

        if (initialAttempt != null)
        {
            try
            {
                initialAttempt.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (ObjectDisposedException)
            {
                // Concurrent Disable/Dispose tore down the signal; return current locked status cleanly.
            }
        }

        return GetStatus();
    }

    /// <summary>
    /// Stops the background singleton-holding thread and releases all Win32 kernel handles.
    /// </summary>
    public MultiInstanceStatus Disable()
    {
        Thread? threadToJoin;
        ManualResetEventSlim? stopEvent;

        lock (_stateLock)
        {
            if (!_enabled && _workerThread is null)
            {
                return GetStatus();
            }

            _enabled = false;
            _ownsMutex = false;
            _waitingForMutex = false;
            stopEvent = _stopEvent;
            threadToJoin = _workerThread;
        }

        stopEvent?.Set();

        if (threadToJoin is { IsAlive: true } && Thread.CurrentThread != threadToJoin)
        {
            threadToJoin.Join(TimeSpan.FromSeconds(5));
        }

        lock (_stateLock)
        {
            if (_workerThread == threadToJoin)
            {
                _workerThread = null;
            }
        }

        return GetStatus();
    }

    /// <summary>
    /// Returns the current multi-instance state, ownership flags, and running Roblox client PIDs.
    /// </summary>
    public MultiInstanceStatus GetStatus()
    {
        IReadOnlyList<int> pids = GetRunningRobloxPids();
        lock (_stateLock)
        {
            return new MultiInstanceStatus(
                Enabled: _enabled,
                OwnsMutex: _ownsMutex,
                WaitingForMutex: _waitingForMutex,
                RunningRobloxPids: pids,
                LastError: _lastError);
        }
    }

    private MultiInstanceStatus GetStatusLocked()
    {
        return new MultiInstanceStatus(
            Enabled: _enabled,
            OwnsMutex: _ownsMutex,
            WaitingForMutex: _waitingForMutex,
            RunningRobloxPids: GetRunningRobloxPidsLocked(),
            LastError: _lastError);
    }

    /// <summary>
    /// Adds a PID to manually tracked processes (for unit testing and process supervision).
    /// </summary>
    public void TrackProcess(int pid)
    {
        lock (_stateLock)
        {
            _manualTrackedPids.Add(pid);
        }
    }

    /// <summary>
    /// Removes a PID from manually tracked processes.
    /// </summary>
    public void UntrackProcess(int pid)
    {
        lock (_stateLock)
        {
            _manualTrackedPids.Remove(pid);
        }
    }

    /// <summary>
    /// Disposes resources, shutting down the background thread and releasing kernel handles cleanly.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Disable();

        lock (_stateLock)
        {
            _stopEvent?.Dispose();
            _stopEvent = null;
            _initialAttemptDone?.Dispose();
            _initialAttemptDone = null;
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private void HoldUntilReleased()
    {
        var stopEvent = _stopEvent!;
        var initialAttempt = _initialAttemptDone!;

        try
        {
            AcquireInitialObjects(stopEvent, initialAttempt);

            while (!stopEvent.IsSet)
            {
                if (stopEvent.Wait(HealIntervalMs))
                {
                    break;
                }

                Heal();
            }
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _lastError = ex.Message;
            }
            try { initialAttempt.Set(); } catch (ObjectDisposedException) { }
        }
        finally
        {
            ReleaseObjects();
        }
    }

    private void AcquireInitialObjects(ManualResetEventSlim stopEvent, ManualResetEventSlim initialAttempt)
    {
        _eventHandle = Kernel32.CreateEventW(nint.Zero, bManualReset: true, bInitialState: false, _eventName);
        if (_eventHandle == nint.Zero)
        {
            lock (_stateLock)
            {
                _lastError = $"CreateEventW failed with Win32 error {Marshal.GetLastWin32Error()}.";
            }
        }
        else
        {
            Kernel32.ResetEvent(_eventHandle);
        }

        _mutexHandle = Kernel32.CreateMutexW(nint.Zero, bInitialOwner: false, _mutexName);
        if (_mutexHandle == nint.Zero)
        {
            lock (_stateLock)
            {
                _lastError = $"CreateMutexW failed with Win32 error {Marshal.GetLastWin32Error()}.";
            }
            try { initialAttempt.Set(); } catch (ObjectDisposedException) { }
            return;
        }

        uint waitResult = Kernel32.WaitForSingleObject(_mutexHandle, 0);
        if (waitResult is Kernel32.WAIT_OBJECT_0 or Kernel32.WAIT_ABANDONED)
        {
            lock (_stateLock)
            {
                _threadHoldsMutex = true;
                if (_enabled && !stopEvent.IsSet)
                {
                    _ownsMutex = true;
                }
                _waitingForMutex = false;
                _lastError = null;
            }
            try { initialAttempt.Set(); } catch (ObjectDisposedException) { }
            return;
        }

        if (waitResult == Kernel32.WAIT_TIMEOUT)
        {
            lock (_stateLock)
            {
                _ownsMutex = false;
                _waitingForMutex = _enabled && !stopEvent.IsSet;
            }
            try { initialAttempt.Set(); } catch (ObjectDisposedException) { }
            WaitForMutexUntilAcquired(stopEvent);
            return;
        }

        lock (_stateLock)
        {
            _lastError = $"WaitForSingleObject failed with result {waitResult} (Win32 error {Marshal.GetLastWin32Error()}).";
        }
        try { initialAttempt.Set(); } catch (ObjectDisposedException) { }
    }

    private void WaitForMutexUntilAcquired(ManualResetEventSlim stopEvent)
    {
        while (!stopEvent.IsSet && _mutexHandle != nint.Zero)
        {
            uint waitResult = Kernel32.WaitForSingleObject(_mutexHandle, MutexPollMs);
            if (waitResult is Kernel32.WAIT_OBJECT_0 or Kernel32.WAIT_ABANDONED)
            {
                lock (_stateLock)
                {
                    _threadHoldsMutex = true;
                    if (!stopEvent.IsSet && _enabled)
                    {
                        _ownsMutex = true;
                    }
                    _waitingForMutex = false;
                    _lastError = null;
                }
                return;
            }

            if (waitResult == Kernel32.WAIT_FAILED)
            {
                lock (_stateLock)
                {
                    _lastError = $"WaitForSingleObject failed with Win32 error {Marshal.GetLastWin32Error()}.";
                    _waitingForMutex = false;
                }
                return;
            }
        }
    }

    private void Heal()
    {
        if (_eventHandle == nint.Zero)
        {
            _eventHandle = Kernel32.CreateEventW(nint.Zero, bManualReset: true, bInitialState: false, _eventName);
            if (_eventHandle == nint.Zero)
            {
                lock (_stateLock)
                {
                    _lastError = $"Heal CreateEventW failed with Win32 error {Marshal.GetLastWin32Error()}.";
                }
            }
            else
            {
                Kernel32.ResetEvent(_eventHandle);
            }
        }

        if (_mutexHandle == nint.Zero)
        {
            _mutexHandle = Kernel32.CreateMutexW(nint.Zero, bInitialOwner: false, _mutexName);
            if (_mutexHandle == nint.Zero)
            {
                lock (_stateLock)
                {
                    _lastError = $"Heal CreateMutexW failed with Win32 error {Marshal.GetLastWin32Error()}.";
                }
                return;
            }
        }

        if (!_threadHoldsMutex && _mutexHandle != nint.Zero)
        {
            uint waitResult = Kernel32.WaitForSingleObject(_mutexHandle, 0);
            if (waitResult is Kernel32.WAIT_OBJECT_0 or Kernel32.WAIT_ABANDONED)
            {
                lock (_stateLock)
                {
                    _threadHoldsMutex = true;
                    if (_enabled && !(_stopEvent?.IsSet ?? false))
                    {
                        _ownsMutex = true;
                    }
                    _waitingForMutex = false;
                    _lastError = null;
                }
            }
            else if (waitResult == Kernel32.WAIT_TIMEOUT)
            {
                lock (_stateLock)
                {
                    if (_enabled && !(_stopEvent?.IsSet ?? false))
                    {
                        _waitingForMutex = true;
                    }
                }
            }
        }
    }

    private void ReleaseObjects()
    {
        if (_mutexHandle != nint.Zero)
        {
            bool hadOwnership;
            lock (_stateLock)
            {
                hadOwnership = _threadHoldsMutex || _ownsMutex;
                _ownsMutex = false;
                _threadHoldsMutex = false;
                _waitingForMutex = false;
            }

            if (hadOwnership)
            {
                Kernel32.ReleaseMutex(_mutexHandle);
            }

            Kernel32.CloseHandle(_mutexHandle);
            _mutexHandle = nint.Zero;
        }

        if (_eventHandle != nint.Zero)
        {
            Kernel32.CloseHandle(_eventHandle);
            _eventHandle = nint.Zero;
        }

        lock (_stateLock)
        {
            _waitingForMutex = false;
        }
    }

    private IReadOnlyList<int> GetRunningRobloxPids()
    {
        lock (_stateLock)
        {
            if (_manualTrackedPids.Count > 0)
            {
                return _manualTrackedPids.OrderBy(p => p).ToList().AsReadOnly();
            }
        }

        return EnumerateExternalPids();
    }

    private IReadOnlyList<int> GetRunningRobloxPidsLocked()
    {
        if (_manualTrackedPids.Count > 0)
        {
            return _manualTrackedPids.OrderBy(p => p).ToList().AsReadOnly();
        }

        return EnumerateExternalPids();
    }

    private IReadOnlyList<int> EnumerateExternalPids()
    {
        if (_processEnumerator != null)
        {
            return _processEnumerator().OrderBy(p => p).ToList().AsReadOnly();
        }

        var pids = new List<int>();
        var processes = Process.GetProcessesByName("RobloxPlayerBeta");
        foreach (var process in processes)
        {
            try
            {
                pids.Add(process.Id);
            }
            finally
            {
                process.Dispose();
            }
        }

        return pids.Distinct().OrderBy(p => p).ToList().AsReadOnly();
    }
}
