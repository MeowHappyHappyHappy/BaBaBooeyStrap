namespace BaBaBooeyStrap.Core.FastFlags;

public interface IFastFlagManager
{
    IReadOnlyDictionary<string, object> Flags { get; }
    void SetFlag(string name, object value);
    bool RemoveFlag(string name);
    void Save();
    void Load();
    string ExportJson();
    void ImportJson(string json);
    void ApplyPreset(string presetName, object value);
}
