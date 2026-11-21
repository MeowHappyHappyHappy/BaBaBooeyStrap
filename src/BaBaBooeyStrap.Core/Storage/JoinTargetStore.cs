using System.Collections.Concurrent;
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

    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);
    private object SyncLock => PathLocks.GetOrAdd(Path.GetFullPath(FilePath), _ => new object());

    public string FilePath { get; }

    public JoinTargetStore(string? filePath = null)
    {
        FilePath = !string.IsNullOrWhiteSpace(filePath)
            ? filePath
            : Paths.TargetsFile;
    }

    public IReadOnlyList<SavedJoinTarget> LoadAll()
    {
        lock (SyncLock)
        {
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

            return JsonSerializer.Deserialize<List<SavedJoinTarget>>(json, JsonOptions) ?? [];
        }
    }

    public void SaveAll(IEnumerable<SavedJoinTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        lock (SyncLock)
        {
            var list = targets.ToList();
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(list, JsonOptions);
            var tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json);

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

    public void Upsert(SavedJoinTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (SyncLock)
        {
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
    }

    public bool Delete(Guid id)
    {
        lock (SyncLock)
        {
            var list = LoadAll().ToList();
            var removed = list.RemoveAll(t => t.Id == id) > 0;
            if (removed)
            {
                SaveAll(list);
            }

            return removed;
        }
    }
}
