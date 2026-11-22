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

    public string FilePath { get; }
    public string? LastLoadError { get; private set; }
    public event Action<string>? OnCorruptionRecovered;

    public DpapiAccountStore(string? filePath = null)
    {
        FilePath = !string.IsNullOrWhiteSpace(filePath)
            ? filePath
            : Paths.AccountsFile;
    }

    public IReadOnlyList<RobloxAccount> LoadAll()
    {
        using var _ = CrossProcessFileLock.Acquire(FilePath);

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

        try
        {
            var plainBytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plainBytes);
            LastLoadError = null;
            return JsonSerializer.Deserialize<List<RobloxAccount>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            LastLoadError = ex.Message;
            string backupPath = BackupCorruptedFile(FilePath);
            OnCorruptionRecovered?.Invoke(backupPath);
            return [];
        }
    }

    public void SaveAll(IEnumerable<RobloxAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        using var _ = CrossProcessFileLock.Acquire(FilePath);

        var json = JsonSerializer.Serialize(accounts.ToList(), JsonOptions);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

        CrossProcessFileLock.AtomicWrite(FilePath, tempPath => File.WriteAllBytes(tempPath, encryptedBytes));
    }

    public RobloxAccount Upsert(RobloxAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var _ = CrossProcessFileLock.Acquire(FilePath);

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

    public RobloxAccount? GetById(Guid id)
    {
        using var _ = CrossProcessFileLock.Acquire(FilePath);
        return LoadAll().FirstOrDefault(a => a.Id == id);
    }

    public bool Delete(Guid id)
    {
        using var _ = CrossProcessFileLock.Acquire(FilePath);

        var list = LoadAll().ToList();
        bool removed = list.RemoveAll(a => a.Id == id) > 0;
        if (removed)
        {
            SaveAll(list);
        }
        return removed;
    }

    private static string BackupCorruptedFile(string filePath)
    {
        string backupPath = $"{filePath}.corrupted.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
        try
        {
            if (File.Exists(filePath))
            {
                File.Move(filePath, backupPath, overwrite: true);
            }
        }
        catch
        {
            try { File.Copy(filePath, backupPath, overwrite: true); } catch { }
        }
        return backupPath;
    }
}
