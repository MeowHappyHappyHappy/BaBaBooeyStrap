using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Core.Storage;

public sealed class DpapiAccountStore : IDpapiAccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);
    private object SyncLock => PathLocks.GetOrAdd(Path.GetFullPath(FilePath), _ => new object());

    public string FilePath { get; }

    public DpapiAccountStore(string? filePath = null)
    {
        FilePath = !string.IsNullOrWhiteSpace(filePath)
            ? filePath
            : Paths.AccountsFile;
    }

    public IReadOnlyList<RobloxAccount> LoadAll()
    {
        lock (SyncLock)
        {
            if (!File.Exists(FilePath)) return [];
            byte[] bytes;
            int retries = 10;
            while (true)
            {
                try
                {
                    if (!File.Exists(FilePath)) return [];
                    using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    bytes = new byte[stream.Length];
                    int totalRead = 0;
                    while (totalRead < bytes.Length)
                    {
                        int read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
                        if (read == 0) break;
                        totalRead += read;
                    }
                    if (totalRead < bytes.Length)
                    {
                        Array.Resize(ref bytes, totalRead);
                    }
                    break;
                }
                catch (IOException) when (retries > 0)
                {
                    retries--;
                    Thread.Sleep(15);
                }
            }
            if (bytes.Length == 0) return [];

            var plainBytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<List<RobloxAccount>>(json, JsonOptions) ?? [];
        }
    }

    public void SaveAll(IEnumerable<RobloxAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        lock (SyncLock)
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(accounts.ToList(), JsonOptions);
            var plainBytes = Encoding.UTF8.GetBytes(json);
            var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

            var tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tempPath, encryptedBytes);

            int retries = 10;
            while (true)
            {
                try
                {
                    File.Move(tempPath, FilePath, overwrite: true);
                    break;
                }
                catch when (retries > 0)
                {
                    retries--;
                    Thread.Sleep(15);
                }
            }
        }
    }

    public RobloxAccount Upsert(RobloxAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (SyncLock)
        {
            var list = LoadAll().ToList();
            int idx = list.FindIndex(a => a.Id == account.Id || (account.UserId > 0 && a.UserId == account.UserId));
            if (idx >= 0)
            {
                list[idx] = account;
            }
            else
            {
                list.Add(account);
            }

            SaveAll(list);
            return account;
        }
    }

    public RobloxAccount? GetById(Guid id)
    {
        lock (SyncLock)
        {
            return LoadAll().FirstOrDefault(a => a.Id == id);
        }
    }

    public bool Delete(Guid id)
    {
        lock (SyncLock)
        {
            var list = LoadAll().ToList();
            bool removed = list.RemoveAll(a => a.Id == id) > 0;
            if (removed)
            {
                SaveAll(list);
            }
            return removed;
        }
    }
}
