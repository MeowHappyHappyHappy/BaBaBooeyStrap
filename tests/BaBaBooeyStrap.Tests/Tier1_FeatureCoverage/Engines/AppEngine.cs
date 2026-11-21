using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

public sealed class LaunchSettingsRouter
{
    public LaunchSettings Settings { get; }

    public LaunchSettingsRouter(string[] args)
    {
        Settings = new LaunchSettings(args ?? Array.Empty<string>());
    }

    public static bool IsValidPlayerProtocol(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        return uri.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase) && uri.Length > "roblox-player:".Length;
    }
}

public sealed class MainWindowViewModel
{
    private readonly IDpapiAccountStore? _accountStore;
    private readonly Settings _settings;

    public ObservableCollection<string> NavigationTabs { get; } = new()
    {
        "Accounts", "FastFlags", "Mods", "Appearance", "Behaviour", "Channel", "Global Settings", "Shortcuts", "About"
    };

    public ObservableCollection<RobloxAccount> Accounts { get; } = new();

    public Theme CurrentTheme
    {
        get => _settings.Theme;
        set
        {
            _settings.Theme = value;
            ThemeChanged?.Invoke(this, value);
        }
    }

    public byte AcrylicBackgroundOpacity
    {
        get => _settings.AcrylicBackgroundOpacity;
        set => _settings.AcrylicBackgroundOpacity = value;
    }

    public bool UseAcrylicBackground
    {
        get => _settings.UseAcrylicBackground;
        set => _settings.UseAcrylicBackground = value;
    }

    public event EventHandler<Theme>? ThemeChanged;

    public MainWindowViewModel(Settings settings, IDpapiAccountStore? accountStore = null)
    {
        _settings = settings ?? new Settings();
        _accountStore = accountStore;
        ReloadAccounts();
    }

    public void ReloadAccounts()
    {
        Accounts.Clear();
        if (_accountStore != null)
        {
            try
            {
                var loaded = _accountStore.LoadAll();
                foreach (var acc in loaded) Accounts.Add(acc);
            }
            catch { }
        }
    }

    public void SaveSettings(IJsonManager<Settings> manager)
    {
        manager.Save();
    }
}

public sealed class FastFlagEditorViewModel
{
    private readonly ObservableCollection<FastFlag> _flags = new();
    private string _searchText = string.Empty;

    public ObservableCollection<FastFlag> Flags => _flags;

    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value ?? string.Empty;
        }
    }

    public IEnumerable<FastFlag> FilteredFlags
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_searchText))
                return _flags;

            string escaped = Regex.Escape(_searchText);
            var regex = new Regex(escaped, RegexOptions.IgnoreCase);
            return _flags.Where(f => regex.IsMatch(f.Name));
        }
    }

    public void AddFlag(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Flag name cannot be empty.", nameof(name));

        name = name.Trim();
        var existing = _flags.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Value = value;
        }
        else
        {
            _flags.Add(new FastFlag { Name = name, Value = value });
        }
    }

    public bool EditFlag(string name, string newValue)
    {
        var flag = _flags.FirstOrDefault(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (flag == null) return false;
        flag.Value = newValue;
        return true;
    }

    public bool DeleteFlag(string name)
    {
        var flag = _flags.FirstOrDefault(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (flag == null) return false;
        return _flags.Remove(flag);
    }

    public bool ImportJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var parsed = new List<FastFlag>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string key = prop.Name.Trim();
                string val = prop.Value.ToString();
                parsed.Add(new FastFlag { Name = key, Value = val });
            }

            // Only mutate if entire JSON parsed cleanly
            _flags.Clear();
            foreach (var f in parsed) _flags.Add(f);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public string ExportJson()
    {
        var dict = new Dictionary<string, string>();
        foreach (var f in _flags)
        {
            dict[f.Name] = f.Value;
        }
        return JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
    }
}

public sealed class NotifyIconWrapper : IDisposable
{
    public bool IsWindowVisible { get; private set; } = true;
    public bool IsTrayActive { get; private set; }
    public string? LastBalloonTitle { get; private set; }
    public string? LastBalloonMessage { get; private set; }

    public IReadOnlyList<string> ContextMenuActions { get; } = new[] { "Settings", "Launch", "Exit" };

    public void Initialize()
    {
        IsTrayActive = true;
    }

    public void MinimizeToTray()
    {
        IsWindowVisible = false;
        IsTrayActive = true;
    }

    public void RestoreWindow()
    {
        IsWindowVisible = true;
    }

    public void ShowBalloonNotification(string title, string message)
    {
        LastBalloonTitle = title;
        LastBalloonMessage = message;
    }

    public void Dispose()
    {
        IsTrayActive = false;
    }
}

public record ThemeDefinition(string Background, string Foreground);

public static class XmlThemeParser
{
    public static ThemeDefinition Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return GetDefaultFluent();

        try
        {
            var doc = XDocument.Parse(xml);
            var colors = doc.Root?.Element("Colors");
            string bg = (string?)colors?.Attribute("Background") ?? "#FF202020";
            string fg = (string?)colors?.Attribute("Foreground") ?? "#FFFFFFFF";
            return new ThemeDefinition(bg, fg);
        }
        catch
        {
            return GetDefaultFluent();
        }
    }

    public static ThemeDefinition GetDefaultFluent() =>
        new ThemeDefinition("#FF202020", "#FFFFFFFF");
}
