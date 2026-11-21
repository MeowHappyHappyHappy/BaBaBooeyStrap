using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaBaBooeyStrap.Core.Storage;

public class JsonManager<T> : IJsonManager<T> where T : class, new()
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly string? _customFileLocation;

    public T OriginalProp { get; private set; } = new();
    public T Prop { get; set; } = new();
    public string? LastFileHash { get; private set; }
    public bool Loaded { get; private set; }

    public virtual string ClassName => typeof(T).Name;
    public virtual string FileLocation => _customFileLocation ?? Path.Combine(Paths.Base, $"{ClassName}.json");

    private object SyncLock => PathLocks.GetOrAdd(Path.GetFullPath(FileLocation), _ => new object());

    public event EventHandler<JsonLoadErrorEventArgs>? OnLoadError;

    public JsonManager(string? customFileLocation = null)
    {
        _customFileLocation = customFileLocation;
    }

    public virtual void Load(bool alertFailure = true)
    {
        lock (SyncLock)
        {
            if (!File.Exists(FileLocation))
            {
                Prop = new T();
                OriginalProp = Clone(Prop);
                Loaded = true;
                Save();
                return;
            }

            try
            {
                string text;
                using (var stream = new FileStream(FileLocation, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidDataException("Configuration file is empty.");
                }

                T? val = JsonSerializer.Deserialize<T>(text, DefaultJsonOptions);
                Prop = val ?? throw new InvalidDataException("Deserialization returned null.");
                OriginalProp = Clone(Prop);
                Loaded = true;
                LastFileHash = ComputeHash(text);
            }
            catch (Exception ex)
            {
                string? backupPath = null;
                try
                {
                    backupPath = $"{FileLocation}.corrupted.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                    File.Copy(FileLocation, backupPath, overwrite: true);
                }
                catch { }

                OnLoadError?.Invoke(this, new JsonLoadErrorEventArgs(FileLocation, ex, backupPath));

                // Fall back to new defaults
                Prop = new T();
                OriginalProp = Clone(Prop);
                Loaded = true;
                Save();
            }
        }
    }

    public virtual void Save()
    {
        lock (SyncLock)
        {
            var dir = Path.GetDirectoryName(FileLocation);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string text = JsonSerializer.Serialize(Prop, DefaultJsonOptions);
            string tempPath = $"{FileLocation}.{Guid.NewGuid():N}.tmp";

            File.WriteAllText(tempPath, text, Encoding.UTF8);

            int retries = 10;
            while (true)
            {
                try
                {
                    File.Move(tempPath, FileLocation, overwrite: true);
                    break;
                }
                catch when (retries > 0)
                {
                    retries--;
                    Thread.Sleep(15);
                }
            }

            LastFileHash = ComputeHash(text);
        }
    }

    public bool HasFileOnDiskChanged()
    {
        lock (SyncLock)
        {
            if (!File.Exists(FileLocation)) return LastFileHash != null;

            using var stream = new FileStream(FileLocation, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string content = reader.ReadToEnd();
            return LastFileHash != ComputeHash(content);
        }
    }

    private static string ComputeHash(string content)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }

    private static T Clone(T source)
    {
        string json = JsonSerializer.Serialize(source, DefaultJsonOptions);
        return JsonSerializer.Deserialize<T>(json, DefaultJsonOptions) ?? new T();
    }
}
