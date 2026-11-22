using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.FastFlags;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.App.ViewModels;

public sealed class FastFlagEditorViewModel : ViewModelBase
{
    private readonly IFastFlagManager? _manager;
    private readonly ObservableCollection<FastFlag> _flags = new();
    private string _searchText = string.Empty;
    private FastFlagType? _selectedTypeFilter;
    private string _statusMessage = string.Empty;

    public FastFlagEditorViewModel() : this(null)
    {
    }

    public FastFlagEditorViewModel(IFastFlagManager? manager)
    {
        _manager = manager;
        FlagsView = CollectionViewSource.GetDefaultView(_flags);
        FlagsView.Filter = FilterFastFlag;
        if (_manager != null)
        {
            LoadFromManager();
        }
    }

    public ObservableCollection<FastFlag> Flags => _flags;
    public ICollectionView FlagsView { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            string normalized = value ?? string.Empty;
            if (SetProperty(ref _searchText, normalized))
            {
                FlagsView.Refresh();
                OnPropertyChanged(nameof(FilteredFlags));
                OnPropertyChanged(nameof(FilteredFlagCount));
            }
        }
    }

    public FastFlagType? SelectedTypeFilter
    {
        get => _selectedTypeFilter;
        set
        {
            if (SetProperty(ref _selectedTypeFilter, value))
            {
                FlagsView.Refresh();
                OnPropertyChanged(nameof(FilteredFlags));
                OnPropertyChanged(nameof(FilteredFlagCount));
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public int TotalFlagCount => _flags.Count;
    public int FilteredFlagCount => FilteredFlags.Count();

    public IEnumerable<FastFlag> FilteredFlags
    {
        get
        {
            IEnumerable<FastFlag> query = _flags;

            if (_selectedTypeFilter.HasValue)
            {
                var filterType = _selectedTypeFilter.Value;
                query = query.Where(f => f.Type == filterType);
            }

            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                query = query.Where(f => f.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
            }

            return query;
        }
    }

    private bool FilterFastFlag(object obj)
    {
        if (obj is not FastFlag flag)
        {
            return false;
        }

        if (_selectedTypeFilter.HasValue && flag.Type != _selectedTypeFilter.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_searchText) &&
            !flag.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public void AddFlag(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(LocalizationService.Instance.GetString("Str_FastFlagEditor_ErrEmptyName"), nameof(name));

        string trimmed = name.Trim();
        var existing = _flags.FirstOrDefault(f => string.Equals(f.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Value = value ?? string.Empty;
        }
        else
        {
            _flags.Add(new FastFlag { Name = trimmed, Value = value ?? string.Empty });
        }

        NotifyCollectionDerivedProperties();
    }

    public bool EditFlag(string name, string newValue)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        string trimmed = name.Trim();
        var flag = _flags.FirstOrDefault(f => string.Equals(f.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (flag == null) return false;

        flag.Value = newValue ?? string.Empty;
        NotifyCollectionDerivedProperties();
        return true;
    }

    public bool DeleteFlag(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        string trimmed = name.Trim();
        var flag = _flags.FirstOrDefault(f => string.Equals(f.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (flag == null) return false;

        bool removed = _flags.Remove(flag);
        if (removed)
        {
            NotifyCollectionDerivedProperties();
        }
        return removed;
    }

    public bool ImportJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var parsed = new List<FastFlag>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string key = prop.Name.Trim();
                if (string.IsNullOrWhiteSpace(key)) continue;
                string val = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.ToString();

                parsed.Add(new FastFlag { Name = key, Value = val });
            }

            _flags.Clear();
            foreach (var flag in parsed)
            {
                _flags.Add(flag);
            }

            NotifyCollectionDerivedProperties();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public string ExportJson()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var flag in _flags)
        {
            dict[flag.Name] = flag.Value;
        }

        return JsonSerializer.Serialize(dict, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    public void LoadFromManager()
    {
        if (_manager == null) return;

        _manager.Load();
        _flags.Clear();
        foreach (var kvp in _manager.Flags.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            _flags.Add(new FastFlag
            {
                Name = kvp.Key,
                Value = kvp.Value?.ToString() ?? string.Empty
            });
        }
        NotifyCollectionDerivedProperties();
    }

    public void SaveToManager()
    {
        if (_manager == null) return;

        foreach (var existingKey in _manager.Flags.Keys.ToList())
        {
            _manager.RemoveFlag(existingKey);
        }

        foreach (var flag in _flags)
        {
            if (!string.IsNullOrWhiteSpace(flag.Name))
            {
                _manager.SetFlag(flag.Name, flag.Value);
            }
        }

        _manager.Save();
        StatusMessage = LocalizationService.Instance.Format("Str_FastFlagEditor_SavedFlagsFormat", _flags.Count);
    }

    public void ClearAll()
    {
        _flags.Clear();
        NotifyCollectionDerivedProperties();
    }

    public void ResetToDefaults()
    {
        _flags.Clear();
        _flags.Add(new FastFlag { Name = "FFlagHandleAltEnterFullscreenManually", Value = "False" });
        NotifyCollectionDerivedProperties();
    }

    public void SaveProfile(string profileName, string profilesDirectory)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException(LocalizationService.Instance.GetString("Str_FastFlagEditor_ErrProfileName"), nameof(profileName));

        Directory.CreateDirectory(profilesDirectory);
        string safeName = string.Concat(profileName.Trim().Split(Path.GetInvalidFileNameChars()));
        if (!safeName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            safeName += ".json";

        string path = Path.Combine(profilesDirectory, safeName);
        File.WriteAllText(path, ExportJson());
        StatusMessage = LocalizationService.Instance.Format("Str_FastFlagEditor_SavedProfileFormat", safeName);
    }

    public bool LoadProfile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        string json = File.ReadAllText(filePath);
        bool ok = ImportJson(json);
        StatusMessage = ok
            ? LocalizationService.Instance.Format("Str_FastFlagEditor_LoadedProfileFormat", Path.GetFileName(filePath))
            : LocalizationService.Instance.GetString("Str_FastFlagEditor_FailedParseProfile");
        return ok;
    }

    private void NotifyCollectionDerivedProperties()
    {
        FlagsView.Refresh();
        OnPropertyChanged(nameof(FilteredFlags));
        OnPropertyChanged(nameof(TotalFlagCount));
        OnPropertyChanged(nameof(FilteredFlagCount));
    }
}
