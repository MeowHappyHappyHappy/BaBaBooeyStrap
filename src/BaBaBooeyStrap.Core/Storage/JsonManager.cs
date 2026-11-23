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

    private readonly string? _customFileLocation;

    public T OriginalProp { get; private set; } = new();
    public T Prop { get; set; } = new();
    public string? LastFileHash { get; private set; }
    public bool Loaded { get; private set; }

    public virtual string ClassName => typeof(T).Name;
    public virtual string FileLocation => _customFileLocation ?? Path.Combine(Paths.Base, $"{ClassName}.json");

    public event EventHandler<JsonLoadErrorEventArgs>? OnLoadError;

    public JsonManager(string? customFileLocation = null)
    {
        _customFileLocation = customFileLocation;
    }

    public virtual void Load(bool alertFailure = true)
    {
        using var fileLock = CrossProcessFileLock.Acquire(FileLocation);

        if (!File.Exists(FileLocation))
        {
            Prop = new T();
            OriginalProp = Clone(Prop);
            Loaded = true;
            SaveUnlocked();
            return;
        }

        try
        {
            string text = ReadAllTextWithRetry(FileLocation);

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException("Configuration file is empty.");
            }

            T? val = JsonSerializer.Deserialize<T>(text, DefaultJsonOptions);
            Prop = val ?? throw new InvalidDataException("Deserialization returned null.");
            OriginalProp = JsonSerializer.Deserialize<T>(text, DefaultJsonOptions) ?? new T();
            Loaded = true;
            LastFileHash = ComputeHash(text);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            string? backupPath = null;
            try
            {
                backupPath = $"{FileLocation}.corrupted.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                File.Copy(FileLocation, backupPath, overwrite: true);
            }
            catch { }

            if (alertFailure)
            {
                OnLoadError?.Invoke(this, new JsonLoadErrorEventArgs(FileLocation, ex, backupPath));
            }

            // Fall back to new defaults
            Prop = new T();
            OriginalProp = Clone(Prop);
            Loaded = true;
            SaveUnlocked();
        }
    }

    public virtual void Save()
    {
        using var fileLock = CrossProcessFileLock.Acquire(FileLocation);
        SaveUnlocked();
    }

    private void SaveUnlocked()
    {
        string text = JsonSerializer.Serialize(Prop, DefaultJsonOptions);
        CrossProcessFileLock.AtomicWrite(FileLocation, tempPath => File.WriteAllText(tempPath, text, Encoding.UTF8));

        OriginalProp = JsonSerializer.Deserialize<T>(text, DefaultJsonOptions) ?? new T();
        LastFileHash = ComputeHash(text);
    }

    public bool HasFileOnDiskChanged()
    {
        using var fileLock = CrossProcessFileLock.Acquire(FileLocation);
        if (!File.Exists(FileLocation)) return LastFileHash != null;

        string content = ReadAllTextWithRetry(FileLocation);
        return LastFileHash != ComputeHash(content);
    }

    private static string ReadAllTextWithRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(Math.Min(150, 10 << Math.Min(attempt, 4)));
            }
        }
    }

    private static string ComputeHash(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }

    private static T Clone(T source)
    {
        string json = JsonSerializer.Serialize(source, DefaultJsonOptions);
        return JsonSerializer.Deserialize<T>(json, DefaultJsonOptions) ?? new T();
    }
}
