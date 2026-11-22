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
        using var _ = CrossProcessFileLock.Acquire(FilePath);

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
            LastLoadError = ex.Message;
            string backupPath = $"{FilePath}.corrupted.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Move(FilePath, backupPath, overwrite: true);
                }
            }
            catch
            {
                try { File.Copy(FilePath, backupPath, overwrite: true); } catch { }
            }
            OnCorruptionRecovered?.Invoke(backupPath);
            return [];
        }
    }

    public void SaveAll(IEnumerable<SavedJoinTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        using var _ = CrossProcessFileLock.Acquire(FilePath);
        var list = targets.ToList();
        var json = JsonSerializer.Serialize(list, JsonOptions);
        CrossProcessFileLock.AtomicWrite(FilePath, tempPath => File.WriteAllText(tempPath, json));
    }

    public void Upsert(SavedJoinTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var _ = CrossProcessFileLock.Acquire(FilePath);
        var list = LoadAll().ToList();
        var index = list.FindIndex(t => t.Id == target.Id);
        if (index >= 0)
        {
            list[index] = target;
        }
        else
        {
            list.Add(target);
        }

        SaveAll(list);
    }

    public bool Delete(Guid id)
    {
        using var _ = CrossProcessFileLock.Acquire(FilePath);
        var list = LoadAll().ToList();
        var removed = list.RemoveAll(t => t.Id == id) > 0;
        if (removed)
        {
            SaveAll(list);
        }

        return removed;
    }
}
