using System.Text;
using System.Text.Json;
using BaBaBooeyStrap.Core.FastFlags;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Bootstrapper.FastFlags;

public sealed class FastFlagManager : IFastFlagManager
{
    private readonly string _filePath;
    private readonly Dictionary<string, object> _flags = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, object> _originalFlags = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public string FilePath => _filePath;

    public IReadOnlyDictionary<string, object> Flags
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, object>(_flags, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public bool Changed
    {
        get
        {
            lock (_lock)
            {
                if (_flags.Count != _originalFlags.Count) return true;
                foreach (var (k, v) in _flags)
                {
                    if (!_originalFlags.TryGetValue(k, out var origVal)) return true;
                    if (!string.Equals(v?.ToString(), origVal?.ToString(), StringComparison.Ordinal)) return true;
                }
                return false;
            }
        }
    }

    public FastFlagManager(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public void SetFlag(string name, object value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Flag name cannot be null or whitespace.", nameof(name));
        }
        ArgumentNullException.ThrowIfNull(value);

        string key = name.Trim();
        lock (_lock)
        {
            _flags[key] = value;
        }
    }

    public bool RemoveFlag(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string key = name.Trim();
        lock (_lock)
        {
            return _flags.Remove(key);
        }
    }

    public object? GetFlag(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_lock)
        {
            return _flags.TryGetValue(name.Trim(), out var val) ? val : null;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _flags.Clear();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = ExportJson();
            string tempFile = $"{_filePath}.{Guid.NewGuid():N}.tmp";

            File.WriteAllText(tempFile, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempFile, _filePath, overwrite: true);

            _originalFlags = new Dictionary<string, object>(_flags, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                _flags.Clear();
                _originalFlags.Clear();
                return;
            }

            string content = File.ReadAllText(_filePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(content))
            {
                _flags.Clear();
                _originalFlags.Clear();
                return;
            }

            try
            {
                ImportJson(content);
                _originalFlags = new Dictionary<string, object>(_flags, StringComparer.OrdinalIgnoreCase);

                // Enforce Alt+Enter fullscreen preset on load
                if (GetFlag("FFlagHandleAltEnterFullscreenManually")?.ToString() != "False")
                {
                    SetFlag("FFlagHandleAltEnterFullscreenManually", "False");
                }
            }
            catch (JsonException)
            {
                // Corrupted JSON recovery: create backup copies and reinitialize cleanly
                string backup = $"{_filePath}.corrupted.{DateTime.UtcNow:yyyyMMdd_HHmmss}.bak";
                string standardBak = $"{_filePath}.bak";
                try { File.Copy(_filePath, backup, true); } catch { }
                try { File.Copy(_filePath, standardBak, true); } catch { }

                _flags.Clear();
                _originalFlags.Clear();
            }
        }
    }

    public string ExportJson()
    {
        lock (_lock)
        {
            var formatted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _flags)
            {
                formatted[kvp.Key] = kvp.Value?.ToString() ?? string.Empty;
            }

            return JsonSerializer.Serialize(formatted, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
    }

    public void ImportJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, options);
        }
        catch (JsonException)
        {
            string trimmed = json.Trim();
            string wrapped = trimmed;
            if (!wrapped.StartsWith("{", StringComparison.Ordinal))
            {
                wrapped = "{\n" + wrapped;
            }
            if (!wrapped.EndsWith("}", StringComparison.Ordinal))
            {
                wrapped = wrapped + "\n}";
            }

            if (wrapped != trimmed)
            {
                doc = JsonDocument.Parse(wrapped, options);
            }
            else
            {
                throw;
            }
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            lock (_lock)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    string key = prop.Name.Trim();
                    switch (prop.Value.ValueKind)
                    {
                        case JsonValueKind.True:
                        case JsonValueKind.False:
                            _flags[key] = prop.Value.GetBoolean();
                            break;
                        case JsonValueKind.Number:
                            if (prop.Value.TryGetInt64(out long lVal))
                            {
                                _flags[key] = lVal;
                            }
                            else
                            {
                                _flags[key] = prop.Value.GetDouble();
                            }
                            break;
                        case JsonValueKind.String:
                            string str = prop.Value.GetString()!;
                            if (bool.TryParse(str, out bool b))
                            {
                                _flags[key] = b;
                            }
                            else if (long.TryParse(str, out long l))
                            {
                                _flags[key] = l;
                            }
                            else
                            {
                                _flags[key] = str;
                            }
                            break;
                        default:
                            _flags[key] = prop.Value.ToString();
                            break;
                    }
                }
            }
        }
    }

    public void ApplyPreset(string presetKey, object value)
    {
        ArgumentNullException.ThrowIfNull(presetKey);
        ArgumentNullException.ThrowIfNull(value);

        if (presetKey.Contains("MSAA", StringComparison.OrdinalIgnoreCase))
        {
            SetFlag("FIntDebugForceMSAASamples", value);
        }
        else if (presetKey.Contains("Vulkan", StringComparison.OrdinalIgnoreCase))
        {
            SetFlag("FFlagDebugGraphicsPreferVulkan", "True");
            RemoveFlag("FFlagDebugGraphicsPreferD3D11");
        }
        else if (presetKey.Contains("D3D11", StringComparison.OrdinalIgnoreCase))
        {
            SetFlag("FFlagDebugGraphicsPreferD3D11", "True");
            RemoveFlag("FFlagDebugGraphicsPreferVulkan");
        }
        else if (presetKey.Contains("FRMQualityOverride", StringComparison.OrdinalIgnoreCase))
        {
            int quality = Math.Clamp(Convert.ToInt32(value), 1, 21);
            SetFlag("DFIntDebugFRMQualityLevelOverride", quality);
        }
        else if (presetKey.Contains("MeshLOD", StringComparison.OrdinalIgnoreCase))
        {
            int num = Math.Clamp(Convert.ToInt32(value), 0, 3);
            SetFlag("DFIntCSGLevelOfDetailSwitchingDistanceStatic", num);
            SetFlag("DFIntCSGLevelOfDetailSwitchingDistance", (Math.Clamp(num - 0, 0, 3) + 1) * 250);
            SetFlag("DFIntCSGLevelOfDetailSwitchingDistanceL12", (Math.Clamp(num - 1, 0, 3) + 1) * 250);
            SetFlag("DFIntCSGLevelOfDetailSwitchingDistanceL23", (Math.Clamp(num - 2, 0, 3) + 1) * 250);
            SetFlag("DFIntCSGLevelOfDetailSwitchingDistanceL34", (Math.Clamp(num - 3, 0, 3) + 1) * 250);
        }
        else if (presetKey.Contains("DisableScaling", StringComparison.OrdinalIgnoreCase))
        {
            SetFlag("DFFlagDisableDPIScale", value);
        }
        else if (presetKey.Contains("ManualFullscreen", StringComparison.OrdinalIgnoreCase))
        {
            SetFlag("FFlagHandleAltEnterFullscreenManually", value);
        }
        else
        {
            SetFlag(presetKey, value);
        }
    }

    public void SetPreset(string prefix, object? value)
    {
        foreach (var item in FastFlagPresets.PresetFlags.Where(x => x.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (value == null)
            {
                RemoveFlag(item.Value);
            }
            else
            {
                SetFlag(item.Value, value);
            }
        }
    }

    public void SetPresetEnum(string prefix, string target, object? value)
    {
        foreach (var item in FastFlagPresets.PresetFlags.Where(x => x.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (item.Key.StartsWith(prefix + "." + target, StringComparison.OrdinalIgnoreCase))
            {
                if (value == null) RemoveFlag(item.Value);
                else SetFlag(item.Value, value);
            }
            else
            {
                RemoveFlag(item.Value);
            }
        }
    }

    public string? GetPreset(string name)
    {
        if (!FastFlagPresets.PresetFlags.TryGetValue(name, out string? flagName))
        {
            return null;
        }
        return GetFlag(flagName)?.ToString();
    }
}
