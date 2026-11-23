using System.Text.Json;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Core.Storage;

/// <summary>
/// Persists named Roblox join links / targets as JSON in <see cref="Paths.TargetsFile"/>.
/// </summary>
public sealed class JoinTargetStore : IJoinTargetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string FilePath { get; }
    public string? LastLoadError { get; private set; }
    public event Action<string>? OnCorruptionRecovered;

    public JoinTargetStore(string? filePath = null)
    {
        FilePath = !string.IsNullOrWhiteSpace(filePath)
            ? filePath
            : Paths.TargetsFile;
    }

    public IReadOnlyList<SavedJoinTarget> LoadAll()
    {
        using var fileLock = CrossProcessFileLock.Acquire(FilePath);
        return LoadAllUnlocked(out _);
    }

    public void SaveAll(IEnumerable<SavedJoinTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        using var _ = CrossProcessFileLock.Acquire(FilePath);
        SaveAllUnlocked(targets.ToList());
    }

    public void Upsert(SavedJoinTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var _ = CrossProcessFileLock.Acquire(FilePath);
        var list = LoadAllUnlocked(out bool hadCorruptError).ToList();
        if (hadCorruptError)
        {
            throw new InvalidOperationException(
                $"Refusing to modify join targets store because '{FilePath}' failed to load: {LastLoadError}");
        }

        var index = list.FindIndex(t => t.Id == target.Id);
        if (index >= 0)
        {
            list[index] = target;
        }
        else
        {
            list.Add(target);
        }

        SaveAllUnlocked(list);
    }

    public bool Delete(Guid id)
    {
        using var _ = CrossProcessFileLock.Acquire(FilePath);
        var list = LoadAllUnlocked(out bool hadCorruptError).ToList();
        if (hadCorruptError)
        {
            throw new InvalidOperationException(
                $"Refusing to delete from join targets store because '{FilePath}' failed to load: {LastLoadError}");
        }

        var removed = list.RemoveAll(t => t.Id == id) > 0;
        if (removed)
        {
            SaveAllUnlocked(list);
        }

        return removed;
    }

    private IReadOnlyList<SavedJoinTarget> LoadAllUnlocked(out bool hadCorruptError)
    {
        hadCorruptError = false;
        if (!File.Exists(FilePath))
        {
            return [];
        }

        string json;
        int retries = 10;
        while (true)
        {
            try
            {
                if (!File.Exists(FilePath)) return [];
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                json = reader.ReadToEnd();
                break;
            }
            catch (IOException) when (retries > 0)
            {
                retries--;
                Thread.Sleep(15);
            }
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            LastLoadError = null;
            return JsonSerializer.Deserialize<List<SavedJoinTarget>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
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
    }

    private void SaveAllUnlocked(List<SavedJoinTarget> list)
    {
        var json = JsonSerializer.Serialize(list, JsonOptions);
        CrossProcessFileLock.AtomicWrite(FilePath, tempPath => File.WriteAllText(tempPath, json));
    }

    private static string? BackupCorruptedFile(string filePath)
    {
        string uniqueSuffix = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid().ToString("N")[..8]}";
        string backupPath = $"{filePath}.corrupted.{uniqueSuffix}.bak";
        try
        {
            if (!File.Exists(filePath)) return null;
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
