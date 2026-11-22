using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

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
            mutex = new Mutex(false, mutexName);

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

            int retries = Math.Max(0, maxMoveRetries);
            while (true)
            {
                try
                {
                    File.Move(tempPath, targetPath, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && retries > 0)
                {
                    retries--;
                    Thread.Sleep(15);
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
