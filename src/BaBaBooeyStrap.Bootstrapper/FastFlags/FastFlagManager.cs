using System.Globalization;
using System.Text;
using System.Text.Json;
using BaBaBooeyStrap.Core.FastFlags;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;

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
        lock (_lock)
        {
            SetFlagUnlocked(name, value);
        }
    }

    public bool RemoveFlag(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_lock)
        {
            return RemoveFlagUnlocked(name);
        }
    }

    private void SetFlagUnlocked(string name, object value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Flag name cannot be null or whitespace.", nameof(name));
        }
        ArgumentNullException.ThrowIfNull(value);

        _flags[name.Trim()] = value;
    }

    private bool RemoveFlagUnlocked(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _flags.Remove(name.Trim());
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
            using var fileLock = CrossProcessFileLock.Acquire(_filePath);
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = ExportJsonUnlocked();
            CrossProcessFileLock.AtomicWrite(_filePath, tempFile =>
            {
                File.WriteAllText(tempFile, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            });

            _originalFlags = new Dictionary<string, object>(_flags, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            using var fileLock = CrossProcessFileLock.Acquire(_filePath);
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
                _flags.Clear();
                ImportJsonUnlocked(content, allowFragmentWrap: false);
                _originalFlags = new Dictionary<string, object>(_flags, StringComparer.OrdinalIgnoreCase);
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

                try
                {
                    CrossProcessFileLock.AtomicWrite(_filePath, tempFile =>
                    {
                        File.WriteAllText(tempFile, "{}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    });
                }
                catch { }
            }
        }
    }

    public string ExportJson()
    {
        lock (_lock)
        {
            return ExportJsonUnlocked();
        }
    }

    private string ExportJsonUnlocked()
    {
        var formatted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _flags)
        {
            formatted[kvp.Key] = kvp.Value switch
            {
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                null => string.Empty,
                _ => kvp.Value.ToString() ?? string.Empty
            };
        }

        return JsonSerializer.Serialize(formatted, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    public void ImportJson(string json)
    {
        lock (_lock)
        {
            ImportJsonUnlocked(json, allowFragmentWrap: true);
        }
    }

    private void ImportJsonUnlocked(string json, bool allowFragmentWrap)
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
        catch (JsonException) when (allowFragmentWrap)
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
                if (!allowFragmentWrap)
                {
                    throw new JsonException("FastFlag root element must be a JSON object.");
                }
                return;
            }

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
                        if (bool.TryParse(str, out bool b) &&
                            string.Equals(str, b.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            _flags[key] = b;
                        }
                        else if (long.TryParse(str, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l) &&
                            string.Equals(str, l.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
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

    public void ApplyPreset(string presetKey, object value)
    {
        ArgumentNullException.ThrowIfNull(presetKey);
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            switch (presetKey.ToLowerInvariant())
            {
                case "rendering.msaa":
                case "msaa":
                    SetFlagUnlocked("FIntDebugForceMSAASamples", value);
                    break;
                case "rendering.mode.vulkan":
                case "rendering.vulkan":
                case "vulkan":
                    if (TryParseBoolValue(value, out bool vulkanEnabled) && !vulkanEnabled)
                    {
                        RemoveFlagUnlocked("FFlagDebugGraphicsPreferVulkan");
                    }
                    else
                    {
                        SetFlagUnlocked("FFlagDebugGraphicsPreferVulkan", "True");
                        RemoveFlagUnlocked("FFlagDebugGraphicsPreferD3D11");
                    }
                    break;
                case "rendering.mode.d3d11":
                case "rendering.d3d11":
                case "d3d11":
                    if (TryParseBoolValue(value, out bool d3d11Enabled) && !d3d11Enabled)
                    {
                        RemoveFlagUnlocked("FFlagDebugGraphicsPreferD3D11");
                    }
                    else
                    {
                        SetFlagUnlocked("FFlagDebugGraphicsPreferD3D11", "True");
                        RemoveFlagUnlocked("FFlagDebugGraphicsPreferVulkan");
                    }
                    break;
                case "rendering.frmqualityoverride":
                case "frmqualityoverride":
                    int quality = Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 1, 21);
                    SetFlagUnlocked("DFIntDebugFRMQualityLevelOverride", quality);
                    break;
                case "geometry.meshlod":
                case "meshlod":
                    int num = Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 0, 3);
                    SetFlagUnlocked("DFIntCSGLevelOfDetailSwitchingDistanceStatic", num);
                    SetFlagUnlocked("DFIntCSGLevelOfDetailSwitchingDistance", (Math.Clamp(num - 0, 0, 3) + 1) * 250);
                    SetFlagUnlocked("DFIntCSGLevelOfDetailSwitchingDistanceL12", (Math.Clamp(num - 1, 0, 3) + 1) * 250);
                    SetFlagUnlocked("DFIntCSGLevelOfDetailSwitchingDistanceL23", (Math.Clamp(num - 2, 0, 3) + 1) * 250);
                    SetFlagUnlocked("DFIntCSGLevelOfDetailSwitchingDistanceL34", (Math.Clamp(num - 3, 0, 3) + 1) * 250);
                    break;
                case "rendering.disablescaling":
                case "disablescaling":
                    SetFlagUnlocked("DFFlagDisableDPIScale", value);
                    break;
                case "rendering.manualfullscreen":
                case "manualfullscreen":
                    SetFlagUnlocked("FFlagHandleAltEnterFullscreenManually", value);
                    break;
                default:
                    SetFlagUnlocked(presetKey, value);
                    break;
            }
        }
    }

    public void SetPreset(string prefix, object? value)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        lock (_lock)
        {
            foreach (var item in FastFlagPresets.PresetFlags.Where(x => MatchesPresetPrefix(x.Key, prefix)))
            {
                if (value == null)
                {
                    RemoveFlagUnlocked(item.Value);
                }
                else
                {
                    SetFlagUnlocked(item.Value, value);
                }
            }
        }
    }

    public void SetPresetEnum(string prefix, string target, object? value)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(target);
        string fullTarget = prefix + "." + target;
        lock (_lock)
        {
            foreach (var item in FastFlagPresets.PresetFlags.Where(x => MatchesPresetPrefix(x.Key, prefix)))
            {
                if (MatchesPresetPrefix(item.Key, fullTarget))
                {
                    if (value == null) RemoveFlagUnlocked(item.Value);
                    else SetFlagUnlocked(item.Value, value);
                }
                else
                {
                    RemoveFlagUnlocked(item.Value);
                }
            }
        }
    }

    private static bool MatchesPresetPrefix(string presetKey, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return false;
        return presetKey.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || presetKey.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseBoolValue(object value, out bool result)
    {
        if (value is bool b)
        {
            result = b;
            return true;
        }
        return bool.TryParse(value.ToString(), out result);
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
