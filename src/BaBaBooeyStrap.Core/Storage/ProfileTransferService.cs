using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaBaBooeyStrap.Core.FastFlags;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Core.Storage;

public sealed class ProfileBundle
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string AppVersion { get; set; } = "1.0.0";
    public DateTimeOffset ExportedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Settings Settings { get; set; } = new();
    public Dictionary<string, object> FastFlags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SavedJoinTarget> SavedTargets { get; set; } = [];
}

/// <summary>
/// Exports and imports user configuration bundles (Settings, FastFlags, and SavedJoinTargets).
/// Account credentials and DPAPI cookies are strictly excluded.
/// </summary>
public static class ProfileTransferService
{
    public const int MaxImportFileBytes = 5 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void ExportToFile(
        string filePath,
        Settings settings,
        IFastFlagManager fastFlagManager,
        IJoinTargetStore joinTargetStore,
        string appVersion = "1.0.0",
        bool includeCustomIntegrations = true,
        bool includePrivateLinks = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fastFlagManager);
        ArgumentNullException.ThrowIfNull(joinTargetStore);

        var clonedSettings = CloneSettings(settings);
        if (!includeCustomIntegrations)
        {
            clonedSettings.CustomIntegrations.Clear();
        }

        var flagsCopy = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in fastFlagManager.Flags)
        {
            if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
            {
                flagsCopy[kvp.Key] = kvp.Value;
            }
        }

        var targetsCopy = joinTargetStore.LoadAll()
            .Where(t => includePrivateLinks ||
                        (!t.TargetInput.Contains("privateServerLinkCode", StringComparison.OrdinalIgnoreCase) &&
                         !t.TargetInput.Contains("/share", StringComparison.OrdinalIgnoreCase)))
            .Select(t => new SavedJoinTarget
            {
                Id = t.Id,
                Name = t.Name,
                TargetInput = t.TargetInput,
                JobId = t.JobId,
                PlaceId = t.PlaceId,
                UserId = t.UserId,
                GameName = t.GameName,
                ThumbnailUrl = t.ThumbnailUrl,
                UpdatedAt = t.UpdatedAt
            })
            .ToList();

        var bundle = new ProfileBundle
        {
            SchemaVersion = ProfileBundle.CurrentSchemaVersion,
            AppVersion = string.IsNullOrWhiteSpace(appVersion) ? "1.0.0" : appVersion.Trim(),
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Settings = clonedSettings,
            FastFlags = flagsCopy,
            SavedTargets = targetsCopy
        };

        string json = JsonSerializer.Serialize(bundle, JsonOptions);
        CrossProcessFileLock.AtomicWrite(
            filePath,
            tempPath => File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
    }

    public static ProfileBundle ImportFromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Profile file not found.", filePath);
        }

        if (info.Length > MaxImportFileBytes)
        {
            throw new InvalidDataException($"Profile file exceeds maximum allowed size ({MaxImportFileBytes / (1024 * 1024)} MB).");
        }

        string json = File.ReadAllText(filePath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("Profile file is empty.");
        }

        ProfileBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<ProfileBundle>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid profile JSON: {ex.Message}", ex);
        }

        if (bundle == null || bundle.Settings == null)
        {
            throw new InvalidDataException("Profile bundle is missing settings.");
        }

        if (bundle.SchemaVersion < 1 || bundle.SchemaVersion > ProfileBundle.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported profile schema version: {bundle.SchemaVersion}.");
        }

        bundle.Settings.RobloxDomain = SanitizeRobloxDomain(bundle.Settings.RobloxDomain);

        var normalizedFlags = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (bundle.FastFlags != null)
        {
            foreach (var kvp in bundle.FastFlags)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null) continue;
                object? normalized = NormalizeJsonValue(kvp.Value);
                if (normalized != null)
                {
                    normalizedFlags[kvp.Key.Trim()] = normalized;
                }
            }
        }

        bundle.FastFlags = normalizedFlags;
        bundle.SavedTargets = (bundle.SavedTargets ?? []).Where(t => t != null).ToList();
        return bundle;
    }

    public static void ApplyImportedProfile(
        ProfileBundle bundle,
        Settings targetSettings,
        IFastFlagManager fastFlagManager,
        IJoinTargetStore joinTargetStore,
        bool replaceSavedTargets = false,
        bool allowCustomIntegrations = false)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(targetSettings);
        ArgumentNullException.ThrowIfNull(fastFlagManager);
        ArgumentNullException.ThrowIfNull(joinTargetStore);

        CopySettingsProperties(bundle.Settings, targetSettings, copyCustomIntegrations: allowCustomIntegrations);

        foreach (string existingKey in fastFlagManager.Flags.Keys.ToList())
        {
            fastFlagManager.RemoveFlag(existingKey);
        }
        foreach (var kvp in bundle.FastFlags)
        {
            fastFlagManager.SetFlag(kvp.Key, kvp.Value);
        }
        fastFlagManager.Save();

        if (replaceSavedTargets)
        {
            joinTargetStore.SaveAll(bundle.SavedTargets);
        }
        else if (bundle.SavedTargets.Count > 0)
        {
            var existing = joinTargetStore.LoadAll().ToList();
            foreach (var incoming in bundle.SavedTargets)
            {
                int idx = existing.FindIndex(t =>
                    t.Id == incoming.Id ||
                    (string.Equals(t.Name, incoming.Name, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(t.TargetInput, incoming.TargetInput, StringComparison.OrdinalIgnoreCase)));
                if (idx >= 0)
                {
                    existing[idx] = incoming;
                }
                else
                {
                    existing.Add(incoming);
                }
            }
            joinTargetStore.SaveAll(existing);
        }
    }

    private static string SanitizeRobloxDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return "roblox.com";
        string trimmed = domain.Trim().TrimEnd('/');
        if (trimmed.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }
        return "roblox.com";
    }

    private static Settings CloneSettings(Settings source)
    {
        string json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? new Settings();
    }

    public static void CopySettingsProperties(Settings source, Settings destination, bool copyCustomIntegrations = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        foreach (PropertyInfo prop in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;

            if (prop.PropertyType == typeof(ObservableCollection<CustomIntegration>))
            {
                if (!copyCustomIntegrations)
                {
                    continue;
                }

                var srcList = prop.GetValue(source) as IEnumerable<CustomIntegration>;
                var dstList = prop.GetValue(destination) as ObservableCollection<CustomIntegration>;
                if (dstList != null)
                {
                    dstList.Clear();
                    if (srcList != null)
                    {
                        foreach (var item in srcList.Where(i => i != null))
                        {
                            dstList.Add(new CustomIntegration
                            {
                                Name = item.Name,
                                Location = item.Location,
                                LaunchArgs = item.LaunchArgs,
                                Delay = item.Delay,
                                PreLaunch = item.PreLaunch,
                                AutoClose = item.AutoClose
                            });
                        }
                    }
                }
                continue;
            }

            if (prop.PropertyType == typeof(List<string>) && prop.CanWrite)
            {
                var srcDirs = prop.GetValue(source) as IEnumerable<string>;
                prop.SetValue(destination, srcDirs != null ? srcDirs.Where(s => s != null).ToList() : []);
                continue;
            }

            if (prop.Name == nameof(Settings.RobloxDomain) && prop.CanWrite)
            {
                prop.SetValue(destination, SanitizeRobloxDomain(prop.GetValue(source) as string));
                continue;
            }

            if (prop.CanWrite)
            {
                prop.SetValue(destination, prop.GetValue(source));
            }
        }
    }

    private static object? NormalizeJsonValue(object value)
    {
        if (value is JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
                JsonValueKind.String => el.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => el.GetRawText()
            };
        }

        return value;
    }
}
