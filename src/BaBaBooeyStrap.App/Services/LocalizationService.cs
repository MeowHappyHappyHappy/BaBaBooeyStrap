using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.App.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    public const string EnglishTag = "en-US";
    public const string TraditionalChineseTag = "zh-TW";

    public static LocalizationService Instance { get; } = new(syncWpfResources: true);

    private readonly bool _syncWpfResources;
    private readonly IReadOnlyDictionary<string, string> _enUsCatalog;
    private readonly IReadOnlyDictionary<string, string> _zhTwCatalog;
    private readonly object _syncLock = new();

    private Dictionary<string, string> _activeStrings;
    private ResourceDictionary? _activeWpfDictionary;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? LanguageChanged;

    internal LocalizationService() : this(syncWpfResources: false)
    {
    }

    private LocalizationService(bool syncWpfResources)
    {
        _syncWpfResources = syncWpfResources;
        _enUsCatalog = LoadEmbeddedCatalog(EnglishTag);
        _zhTwCatalog = LoadEmbeddedCatalog(TraditionalChineseTag);
        _activeStrings = new Dictionary<string, string>(_enUsCatalog, StringComparer.Ordinal);
        SelectedLanguage = AppLanguage.English;
        CurrentLanguageTag = EnglishTag;
    }

    public AppLanguage SelectedLanguage { get; private set; }
    public string CurrentLanguageTag { get; private set; }

    public string this[string key] => GetString(key);

    public static string ResolveEffectiveLanguageTag(AppLanguage language, CultureInfo? systemCulture = null)
    {
        switch (language)
        {
            case AppLanguage.TraditionalChinese:
                return TraditionalChineseTag;
            case AppLanguage.English:
                return EnglishTag;
            case AppLanguage.Auto:
            default:
                var culture = systemCulture ?? CultureInfo.CurrentUICulture;
                if (IsTraditionalChineseCulture(culture))
                    return TraditionalChineseTag;
                return EnglishTag;
        }
    }

    private static bool IsTraditionalChineseCulture(CultureInfo? culture)
    {
        if (culture == null) return false;

        CultureInfo? current = culture;
        while (current != null && !string.IsNullOrEmpty(current.Name))
        {
            string name = current.Name;
            if (name.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current.Equals(current.Parent))
                break;
            current = current.Parent;
        }

        return false;
    }

    public void ApplyLanguage(AppLanguage language, CultureInfo? systemCulture = null)
    {
        string effectiveTag = ResolveEffectiveLanguageTag(language, systemCulture);
        Dictionary<string, string> merged;

        lock (_syncLock)
        {
            SelectedLanguage = language;
            CurrentLanguageTag = effectiveTag;

            merged = new Dictionary<string, string>(_enUsCatalog, StringComparer.Ordinal);
            if (string.Equals(effectiveTag, TraditionalChineseTag, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var kvp in _zhTwCatalog)
                {
                    merged[kvp.Key] = kvp.Value;
                }
            }

            _activeStrings = merged;
        }

        if (_syncWpfResources)
        {
            ApplyWpfResourceDictionary(merged);
        }

        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(CurrentLanguageTag));
        OnPropertyChanged("Item[]");
        LanguageChanged?.Invoke(this, effectiveTag);
    }

    public string GetString(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        var active = _activeStrings;
        if (active.TryGetValue(key, out string? value))
            return value;

        if (_enUsCatalog.TryGetValue(key, out string? fallback))
            return fallback;

        return key;
    }

    public string Format(string key, params object?[] args)
    {
        string template = GetString(key);
        if (args == null || args.Length == 0)
            return template;

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public string GetEnumDisplay(object? enumValue)
    {
        if (enumValue == null) return string.Empty;

        Type enumType = enumValue.GetType();
        string key = $"Enum_{enumType.Name}_{enumValue}";
        var active = _activeStrings;
        if (active.TryGetValue(key, out string? value))
            return value;

        if (_enUsCatalog.TryGetValue(key, out string? fallback))
            return fallback;

        return enumValue.ToString() ?? string.Empty;
    }

    public IReadOnlyDictionary<string, string> GetCatalog(string languageTag)
    {
        return string.Equals(languageTag, TraditionalChineseTag, StringComparison.OrdinalIgnoreCase)
            ? _zhTwCatalog
            : _enUsCatalog;
    }

    private void ApplyWpfResourceDictionary(Dictionary<string, string> strings)
    {
        var app = Application.Current;
        if (app == null) return;

        void Swap()
        {
            var dict = new ResourceDictionary();
            foreach (var kvp in strings)
            {
                dict[kvp.Key] = kvp.Value;
            }

            var mergedDictionaries = app.Resources.MergedDictionaries;
            if (_activeWpfDictionary != null)
            {
                mergedDictionaries.Remove(_activeWpfDictionary);
            }

            mergedDictionaries.Add(dict);
            _activeWpfDictionary = dict;
        }

        var dispatcher = app.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished || !dispatcher.Thread.IsAlive)
            return;

        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Swap);
        }
        else
        {
            Swap();
        }
    }

    private static IReadOnlyDictionary<string, string> LoadEmbeddedCatalog(string languageTag)
    {
        var asm = typeof(LocalizationService).Assembly;
        string suffix = $".{languageTag}.json";
        string? resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using Stream? stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        return parsed != null
            ? new Dictionary<string, string>(parsed, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
