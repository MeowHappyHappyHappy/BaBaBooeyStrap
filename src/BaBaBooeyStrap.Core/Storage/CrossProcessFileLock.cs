using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using BaBaBooeyStrap.Core.Interop;
using Microsoft.Win32.SafeHandles;

namespace BaBaBooeyStrap.Core.Storage;

internal static class CrossProcessFileLock
{
    private static readonly ConcurrentDictionary<string, object> InProcessLocks = new(StringComparer.OrdinalIgnoreCase);

    public static IDisposable Acquire(string filePath, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string fullPath = Path.GetFullPath(filePath);
        object syncLock = InProcessLocks.GetOrAdd(fullPath, _ => new object());
        TimeSpan waitTimeout = timeout ?? TimeSpan.FromSeconds(15);

        bool monitorTaken = false;
        Mutex? mutex = null;
        bool mutexAcquired = false;

        try
        {
            Monitor.TryEnter(syncLock, waitTimeout, ref monitorTaken);
            if (!monitorTaken)
            {
                throw new TimeoutException($"Timed out waiting for in-process file lock on '{fullPath}'.");
            }

            string mutexName = BuildMutexName(fullPath);
            mutex = CreateCurrentUserMutex(mutexName);

            try
            {
                mutexAcquired = mutex.WaitOne(waitTimeout);
            }
            catch (AbandonedMutexException)
            {
                // Abandoned mutex still transfers ownership to this thread cleanly.
                mutexAcquired = true;
            }

            if (!mutexAcquired)
            {
                throw new TimeoutException($"Timed out waiting for cross-process file lock on '{fullPath}'.");
            }

            return new LockScope(syncLock, mutex);
        }
        catch
        {
            if (mutex != null)
            {
                if (mutexAcquired)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
                mutex.Dispose();
            }

            if (monitorTaken)
            {
                Monitor.Exit(syncLock);
            }

            throw;
        }
    }

    public static void AtomicWrite(string targetPath, Action<string> writeTempFile, int maxMoveRetries = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(writeTempFile);

        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            writeTempFile(tempPath);

            if (File.Exists(tempPath))
            {
                using var flushStream = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                flushStream.Flush(flushToDisk: true);
            }

            int retries = Math.Max(0, maxMoveRetries);
            int attempt = 0;
            while (true)
            {
                try
                {
                    File.Move(tempPath, targetPath, overwrite: true);
                    break;
                }
                catch (UnauthorizedAccessException) when (retries == 0)
                {
                    // Sandboxed virtualized folders (e.g., AppContainer AC\) allow CreateFile/WriteFile
                    // via File.Copy while blocking rename syscalls on File.Move. Preserve ReadOnly checks.
                    bool targetIsReadOnly = File.Exists(targetPath) &&
                        (File.GetAttributes(targetPath) & FileAttributes.ReadOnly) != 0;
                    if (targetIsReadOnly)
                    {
                        throw;
                    }

                    File.Copy(tempPath, targetPath, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && retries > 0)
                {
                    retries--;
                    Thread.Sleep(Math.Min(80, 10 << Math.Min(attempt, 3)));
                    attempt++;
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch { }
        }
    }

    private static string BuildMutexName(string fullPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant()));
        return $"Local\\BaBaBooeyStrap_FileLock_{Convert.ToHexString(hash)[..32]}";
    }

    private static Mutex CreateCurrentUserMutex(string mutexName)
    {
        nint saPtr = nint.Zero;
        GCHandle sdHandle = default;
        nint mutexHandle = nint.Zero;

        try
        {
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid != null)
            {
                var dacl = new DiscretionaryAcl(isContainer: false, isDS: false, capacity: 1);
                dacl.AddAccess(
                    AccessControlType.Allow,
                    sid,
                    unchecked((int)Kernel32.MUTEX_ALL_ACCESS),
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

                mutexHandle = Kernel32.CreateMutexW(saPtr, bInitialOwner: false, mutexName);
                if (mutexHandle != nint.Zero)
                {
                    var managedMutex = new Mutex(initiallyOwned: false);
                    var placeholderHandle = managedMutex.SafeWaitHandle;
                    managedMutex.SafeWaitHandle = new SafeWaitHandle(mutexHandle, ownsHandle: true);
                    mutexHandle = nint.Zero;
                    placeholderHandle.Dispose();
                    return managedMutex;
                }
            }
        }
        catch
        {
            if (mutexHandle != nint.Zero)
            {
                Kernel32.CloseHandle(mutexHandle);
            }
        }
        finally
        {
            if (saPtr != nint.Zero)
            {
                Marshal.FreeHGlobal(saPtr);
            }
            if (sdHandle.IsAllocated)
            {
                sdHandle.Free();
            }
        }

        return new Mutex(false, mutexName);
    }

    private sealed class LockScope : IDisposable
    {
        private readonly object _syncLock;
        private readonly Mutex _mutex;
        private int _disposed;

        public LockScope(object syncLock, Mutex mutex)
        {
            _syncLock = syncLock;
            _mutex = mutex;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _mutex.ReleaseMutex();
            }
            catch { }
            finally
            {
                _mutex.Dispose();
                Monitor.Exit(_syncLock);
            }
        }
    }
}
