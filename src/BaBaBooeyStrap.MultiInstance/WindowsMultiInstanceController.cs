using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.MultiInstance.Abstractions;

namespace BaBaBooeyStrap.MultiInstance;

/// <summary>
/// Preemptively holds <c>ROBLOX_singletonMutex</c> and <c>ROBLOX_singletonEvent</c> on a dedicated
/// background thread so multiple RobloxPlayerBeta client instances can run concurrently without tripping Byfron anti-cheat.
/// </summary>
public sealed class WindowsMultiInstanceController : IMultiInstanceController, IDisposable
{
    public const string DefaultMutexName = "ROBLOX_singletonMutex";
    public const string DefaultEventName = "ROBLOX_singletonEvent";

    private const int MutexPollMs = 100;
    private const int HealIntervalMs = 2000;

    private sealed class WorkerState
    {
        public nint MutexHandle;
        public nint EventHandle;
        public bool ThreadHoldsMutex;
    }

    private readonly string _mutexName;
    private readonly string _eventName;
    private readonly Func<IReadOnlyList<int>>? _processEnumerator;
    private readonly object _stateLock = new();
    private readonly HashSet<int> _manualTrackedPids = new();

    private Thread? _workerThread;
    private ManualResetEventSlim? _stopEvent;
    private ManualResetEventSlim? _initialAttemptDone;

    private volatile bool _enabled;
    private volatile bool _ownsMutex;
    private volatile bool _waitingForMutex;
    private volatile string? _lastError;
    private volatile bool _disposed;

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
    /// Starts the dedicated background thread and preemptively acquires the mutex and event.
    /// </summary>
    public MultiInstanceStatus Enable()
    {
        ManualResetEventSlim? initialAttempt = null;
        bool returnCurrentStatusImmediately = false;

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
                        returnCurrentStatusImmediately = true;
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

                    var stopEvent = new ManualResetEventSlim(false);
                    var initialDone = new ManualResetEventSlim(false);
                    _stopEvent = stopEvent;
                    _initialAttemptDone = initialDone;
                    initialAttempt = initialDone;

                    _enabled = true;
                    _ownsMutex = false;
                    _waitingForMutex = false;
                    _lastError = null;

                    _workerThread = new Thread(() => HoldUntilReleased(stopEvent, initialDone))
                    {
                        Name = "multi-roblox-singleton",
                        IsBackground = true
                    };
                    _workerThread.Start();
                    break;
                }
            }

            if (priorThread is { IsAlive: true } && Thread.CurrentThread != priorThread)
            {
                priorThread.Join(TimeSpan.FromSeconds(5));
            }
        }

        if (!returnCurrentStatusImmediately && initialAttempt != null)
        {
            try
            {
                initialAttempt.Wait(TimeSpan.FromSeconds(2));
            }
            catch (ObjectDisposedException)
            {
                // Concurrent Disable/Dispose tore down the signal; return current status cleanly.
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
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        Disable();

        lock (_stateLock)
        {
            _stopEvent?.Dispose();
            _stopEvent = null;
            _initialAttemptDone?.Dispose();
            _initialAttemptDone = null;
        }

        GC.SuppressFinalize(this);
    }

    private void HoldUntilReleased(ManualResetEventSlim stopEvent, ManualResetEventSlim initialAttempt)
    {
        var state = new WorkerState();

        try
        {
            AcquireInitialObjects(state, stopEvent, initialAttempt);

            while (!stopEvent.IsSet)
            {
                if (stopEvent.Wait(HealIntervalMs))
                {
                    break;
                }

                Heal(state, stopEvent);
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
            ReleaseObjects(state);
        }
    }

    private void AcquireInitialObjects(WorkerState state, ManualResetEventSlim stopEvent, ManualResetEventSlim initialAttempt)
    {
        state.EventHandle = CreateCurrentUserEvent();
        if (state.EventHandle == nint.Zero)
        {
            lock (_stateLock)
            {
                _lastError = $"CreateEventW failed with Win32 error {Marshal.GetLastWin32Error()}.";
            }
        }
        else
        {
            Kernel32.ResetEvent(state.EventHandle);
        }

        state.MutexHandle = CreateCurrentUserMutex();
        if (state.MutexHandle == nint.Zero)
        {
            lock (_stateLock)
            {
                _lastError = $"CreateMutexW failed with Win32 error {Marshal.GetLastWin32Error()}.";
            }
            try { initialAttempt.Set(); } catch (ObjectDisposedException) { }
            return;
        }

        uint waitResult = Kernel32.WaitForSingleObject(state.MutexHandle, 0);
        if (waitResult is Kernel32.WAIT_OBJECT_0 or Kernel32.WAIT_ABANDONED)
        {
            lock (_stateLock)
            {
                state.ThreadHoldsMutex = true;
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
            WaitForMutexUntilAcquired(state, stopEvent);
            return;
        }

        lock (_stateLock)
        {
            _lastError = $"WaitForSingleObject failed with result {waitResult} (Win32 error {Marshal.GetLastWin32Error()}).";
        }
        try { initialAttempt.Set(); } catch (ObjectDisposedException) { }
    }

    private void WaitForMutexUntilAcquired(WorkerState state, ManualResetEventSlim stopEvent)
    {
        while (!stopEvent.IsSet && state.MutexHandle != nint.Zero)
        {
            uint waitResult = Kernel32.WaitForSingleObject(state.MutexHandle, MutexPollMs);
            if (waitResult is Kernel32.WAIT_OBJECT_0 or Kernel32.WAIT_ABANDONED)
            {
                lock (_stateLock)
                {
                    state.ThreadHoldsMutex = true;
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

    private void Heal(WorkerState state, ManualResetEventSlim stopEvent)
    {
        if (stopEvent.IsSet)
        {
            return;
        }

        if (state.EventHandle == nint.Zero)
        {
            state.EventHandle = CreateCurrentUserEvent();
            if (state.EventHandle == nint.Zero)
            {
                lock (_stateLock)
                {
                    _lastError = $"Heal CreateEventW failed with Win32 error {Marshal.GetLastWin32Error()}.";
                }
            }
        }

        if (state.EventHandle != nint.Zero)
        {
            Kernel32.ResetEvent(state.EventHandle);
        }

        if (state.MutexHandle == nint.Zero)
        {
            state.MutexHandle = CreateCurrentUserMutex();
            if (state.MutexHandle == nint.Zero)
            {
                lock (_stateLock)
                {
                    _lastError = $"Heal CreateMutexW failed with Win32 error {Marshal.GetLastWin32Error()}.";
                }
                return;
            }
        }

        if (!state.ThreadHoldsMutex && state.MutexHandle != nint.Zero)
        {
            uint waitResult = Kernel32.WaitForSingleObject(state.MutexHandle, 0);
            if (waitResult is Kernel32.WAIT_OBJECT_0 or Kernel32.WAIT_ABANDONED)
            {
                lock (_stateLock)
                {
                    state.ThreadHoldsMutex = true;
                    if (_enabled && !stopEvent.IsSet)
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
                    if (_enabled && !stopEvent.IsSet)
                    {
                        _waitingForMutex = true;
                    }
                }
            }
        }
    }

    private void ReleaseObjects(WorkerState state)
    {
        if (state.MutexHandle != nint.Zero)
        {
            bool hadOwnership;
            lock (_stateLock)
            {
                hadOwnership = state.ThreadHoldsMutex || _ownsMutex;
                _ownsMutex = false;
                state.ThreadHoldsMutex = false;
                _waitingForMutex = false;
            }

            if (hadOwnership)
            {
                Kernel32.ReleaseMutex(state.MutexHandle);
            }

            Kernel32.CloseHandle(state.MutexHandle);
            state.MutexHandle = nint.Zero;
        }

        if (state.EventHandle != nint.Zero)
        {
            Kernel32.CloseHandle(state.EventHandle);
            state.EventHandle = nint.Zero;
        }

        lock (_stateLock)
        {
            _waitingForMutex = false;
        }
    }

    private nint CreateCurrentUserMutex() =>
        WithCurrentUserSecurityAttributes(isEvent: false, sa => Kernel32.CreateMutexW(sa, bInitialOwner: false, _mutexName));

    private nint CreateCurrentUserEvent() =>
        WithCurrentUserSecurityAttributes(isEvent: true, sa => Kernel32.CreateEventW(sa, bManualReset: true, bInitialState: false, _eventName));

    private static nint WithCurrentUserSecurityAttributes(bool isEvent, Func<nint, nint> factory)
    {
        nint saPtr = nint.Zero;
        GCHandle sdHandle = default;

        try
        {
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid != null)
            {
                var dacl = new DiscretionaryAcl(isContainer: false, isDS: false, capacity: 1);
                dacl.AddAccess(
                    AccessControlType.Allow,
                    sid,
                    unchecked((int)(isEvent ? Kernel32.EVENT_ALL_ACCESS : Kernel32.MUTEX_ALL_ACCESS)),
                    InheritanceFlags.None,
                    PropagationFlags.None);
                var sd = new CommonSecurityDescriptor(
                    isContainer: false,
                    isDS: false,
                    flags: ControlFlags.DiscretionaryAclPresent,
                    owner: sid,
                    group: null,
                    systemAcl: null,
                    discretionaryAcl: dacl);
                byte[] sdBytes = new byte[sd.BinaryLength];
                sd.GetBinaryForm(sdBytes, 0);

                sdHandle = GCHandle.Alloc(sdBytes, GCHandleType.Pinned);
                var sa = new Kernel32.SECURITY_ATTRIBUTES
                {
                    nLength = (uint)Marshal.SizeOf<Kernel32.SECURITY_ATTRIBUTES>(),
                    lpSecurityDescriptor = sdHandle.AddrOfPinnedObject(),
                    bInheritHandle = false
                };
                saPtr = Marshal.AllocHGlobal((int)sa.nLength);
                Marshal.StructureToPtr(sa, saPtr, false);
            }
        }
        catch
        {
            if (saPtr != nint.Zero)
            {
                Marshal.FreeHGlobal(saPtr);
                saPtr = nint.Zero;
            }
            if (sdHandle.IsAllocated)
            {
                sdHandle.Free();
                sdHandle = default;
            }
        }

        try
        {
            return factory(saPtr);
        }
        finally
        {
            if (saPtr != nint.Zero) Marshal.FreeHGlobal(saPtr);
            if (sdHandle.IsAllocated) sdHandle.Free();
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
            catch
            {
                // Process exited concurrently
            }
            finally
            {
                process.Dispose();
            }
        }

        return pids.Distinct().OrderBy(p => p).ToList().AsReadOnly();
    }
}
