using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Core.Storage;

public sealed class DpapiAccountStore : IDpapiAccountStore
{
    private const int MaxStoreFileBytes = 16 * 1024 * 1024;
    private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("BaBaBooeyStrap.AccountStore.v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _cacheLock = new();
    private Dictionary<long, RobloxAccount>? _metadataCache;
    private DateTime _cachedWriteTimeUtc;
    private long _cachedFileLength;
    private long _cachedAtTicks;

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
        using var fileLock = CrossProcessFileLock.Acquire(FilePath);
        return LoadAllUnlocked(out _);
    }

    public void SaveAll(IEnumerable<RobloxAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        using var _ = CrossProcessFileLock.Acquire(FilePath);
        SaveAllUnlocked(accounts.ToList());
    }

    public RobloxAccount Upsert(RobloxAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var _ = CrossProcessFileLock.Acquire(FilePath);

        var list = LoadAllUnlocked(out bool hadCorruptError).ToList();
        if (hadCorruptError)
        {
            throw new InvalidOperationException(
                $"Refusing to modify account store because existing store '{FilePath}' failed to load: {LastLoadError}");
        }

        int idx = list.FindIndex(a => a.Id == account.Id || (account.UserId > 0 && a.UserId == account.UserId));
        if (idx >= 0)
        {
            list[idx] = account;
        }
        else
        {
            list.Add(account);
        }

        SaveAllUnlocked(list);
        return account;
    }

    public bool UpdateLastLaunchedAt(Guid id, DateTimeOffset timestamp)
    {
        using var fileLock = CrossProcessFileLock.Acquire(FilePath);
        var list = LoadAllUnlocked(out bool hadCorruptError).ToList();
        if (hadCorruptError)
        {
            return false;
        }

        int idx = list.FindIndex(a => a.Id == id);
        if (idx < 0)
        {
            return false;
        }

        list[idx].LastLaunchedAt = timestamp;
        SaveAllUnlocked(list);
        return true;
    }

    public RobloxAccount? GetById(Guid id)
    {
        using var fileLock = CrossProcessFileLock.Acquire(FilePath);
        return LoadAllUnlocked(out _).FirstOrDefault(a => a.Id == id);
    }

    public RobloxAccount? GetMetadataByUserId(long userId)
    {
        if (userId <= 0) return null;

        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists) return null;

            long nowTicks = Environment.TickCount64;
            lock (_cacheLock)
            {
                if (_metadataCache != null &&
                    _cachedWriteTimeUtc == info.LastWriteTimeUtc &&
                    _cachedFileLength == info.Length &&
                    nowTicks - _cachedAtTicks < 2000)
                {
                    return _metadataCache.TryGetValue(userId, out var cached) ? CloneMetadata(cached) : null;
                }
            }
        }
        catch
        {
            return null;
        }

        IReadOnlyList<RobloxAccount> accounts;
        try
        {
            using var _ = CrossProcessFileLock.Acquire(FilePath);
            accounts = LoadAllUnlocked(out bool hadCorruptError);
            if (hadCorruptError) return null;
        }
        catch
        {
            return null;
        }

        var map = new Dictionary<long, RobloxAccount>();
        foreach (var acc in accounts)
        {
            if (acc.UserId > 0)
            {
                map[acc.UserId] = CloneMetadata(acc);
            }
        }

        try
        {
            var freshInfo = new FileInfo(FilePath);
            lock (_cacheLock)
            {
                _metadataCache = map;
                _cachedWriteTimeUtc = freshInfo.Exists ? freshInfo.LastWriteTimeUtc : DateTime.MinValue;
                _cachedFileLength = freshInfo.Exists ? freshInfo.Length : 0;
                _cachedAtTicks = Environment.TickCount64;
            }
        }
        catch { }

        return map.TryGetValue(userId, out var found) ? CloneMetadata(found) : null;
    }

    public bool Delete(Guid id)
    {
        using var _ = CrossProcessFileLock.Acquire(FilePath);

        var list = LoadAllUnlocked(out bool hadCorruptError).ToList();
        if (hadCorruptError)
        {
            throw new InvalidOperationException(
                $"Refusing to delete from account store because existing store '{FilePath}' failed to load: {LastLoadError}");
        }

        bool removed = list.RemoveAll(a => a.Id == id) > 0;
        if (removed)
        {
            SaveAllUnlocked(list);
        }
        return removed;
    }

    private IReadOnlyList<RobloxAccount> LoadAllUnlocked(out bool hadCorruptError)
    {
        hadCorruptError = false;
        if (!File.Exists(FilePath)) return [];

        byte[] bytes;
        int retries = 10;
        while (true)
        {
            try
            {
                if (!File.Exists(FilePath)) return [];
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length > MaxStoreFileBytes)
                {
                    throw new InvalidDataException($"Account store exceeds maximum allowed size ({stream.Length} bytes).");
                }
                int expectedLen = (int)stream.Length;
                bytes = new byte[expectedLen];
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

        byte[]? plainBytes = null;
        try
        {
            try
            {
                plainBytes = ProtectedData.Unprotect(bytes, AppEntropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // Transparent fallback for legacy stores protected without app-specific entropy
                plainBytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            }

            string json = Encoding.UTF8.GetString(plainBytes);
            LastLoadError = null;
            return JsonSerializer.Deserialize<List<RobloxAccount>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException)
        {
            hadCorruptError = true;
            LastLoadError = ex.Message;
            string? backupPath = BackupCorruptedFile(FilePath);
            if (!string.IsNullOrWhiteSpace(backupPath))
            {
                OnCorruptionRecovered?.Invoke(backupPath);
            }
            return [];
        }
        finally
        {
            if (plainBytes != null)
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
    }

    private void SaveAllUnlocked(List<RobloxAccount> accounts)
    {
        string json = JsonSerializer.Serialize(accounts, JsonOptions);
        byte[] plainBytes = Encoding.UTF8.GetBytes(json);
        byte[] encryptedBytes;
        try
        {
            encryptedBytes = ProtectedData.Protect(plainBytes, AppEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }

        CrossProcessFileLock.AtomicWrite(FilePath, tempPath =>
        {
            File.WriteAllBytes(tempPath, encryptedBytes);
            TryApplyCurrentUserAcl(tempPath);
        });
        TryApplyCurrentUserAcl(FilePath);

        lock (_cacheLock)
        {
            _metadataCache = null;
        }
    }

    private static RobloxAccount CloneMetadata(RobloxAccount source) => new()
    {
        Id = source.Id,
        Username = source.Username,
        DisplayName = source.DisplayName,
        UserId = source.UserId,
        Cookie = string.Empty,
        Notes = source.Notes,
        CreatedAt = source.CreatedAt,
        LastLaunchedAt = source.LastLaunchedAt,
        RobloxSettings = source.RobloxSettings?.Clone()
    };

    private static void TryApplyCurrentUserAcl(string path)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var userSid = identity.User;
            if (userSid == null) return;

            var info = new FileInfo(path);
            if (!info.Exists) return;

            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                userSid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch
        {
            // Best-effort: some virtual/non-NTFS drives may not support FileSecurity
        }
    }

    private static string? BackupCorruptedFile(string filePath)
    {
        string uniqueSuffix = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid().ToString("N")[..8]}";
        string backupPath = $"{filePath}.corrupted.{uniqueSuffix}.bak";
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            File.Move(filePath, backupPath, overwrite: false);
            return File.Exists(backupPath) ? backupPath : null;
        }
        catch
        {
            try
            {
                File.Copy(filePath, backupPath, overwrite: false);
                return File.Exists(backupPath) ? backupPath : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
