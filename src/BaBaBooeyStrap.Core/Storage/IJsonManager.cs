namespace BaBaBooeyStrap.Core.Storage;

public class JsonLoadErrorEventArgs : EventArgs
{
    public string FilePath { get; }
    public Exception Exception { get; }
    public string? BackupPath { get; }

    public JsonLoadErrorEventArgs(string filePath, Exception exception, string? backupPath)
    {
        FilePath = filePath;
        Exception = exception;
        BackupPath = backupPath;
    }
}

public interface IJsonManager<T> where T : class, new()
{
    T Prop { get; set; }
    T OriginalProp { get; }
    string FileLocation { get; }
    bool Loaded { get; }
    void Load(bool alertFailure = true);
    void Save();
    bool HasFileOnDiskChanged();
    event EventHandler<JsonLoadErrorEventArgs>? OnLoadError;
}
