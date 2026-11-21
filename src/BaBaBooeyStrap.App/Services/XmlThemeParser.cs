using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BaBaBooeyStrap.App.Models;

namespace BaBaBooeyStrap.App.Services;

public static partial class XmlThemeParser
{
    public const string DefaultBackground = "#FF202020";
    public const string DefaultForeground = "#FFFFFFFF";
    public const string DefaultAccent = "#FF0078D4";

    [GeneratedRegex("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexColorRegex();

    public static ThemeDefinition GetDefaultFluent() =>
        new(DefaultBackground, DefaultForeground, "Fluent", DefaultAccent);

    public static ThemeDefinition Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return GetDefaultFluent();

        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.None);
            var root = doc.Root;
            if (root == null)
                return GetDefaultFluent();

            if (root.Name.LocalName.Equals("Theme", StringComparison.OrdinalIgnoreCase))
            {
                string name = (string?)root.Attribute("Name") ?? "Custom";
                var colors = root.Element("Colors");
                string bg = ValidateColor((string?)colors?.Attribute("Background") ?? (string?)root.Attribute("Background"), DefaultBackground);
                string fg = ValidateColor((string?)colors?.Attribute("Foreground") ?? (string?)root.Attribute("Foreground"), DefaultForeground);
                string accent = ValidateColor((string?)colors?.Attribute("Accent") ?? (string?)root.Attribute("Accent"), DefaultAccent);
                double width = ParseDouble((string?)root.Attribute("Width"), 500, 200, 1600);
                double height = ParseDouble((string?)root.Attribute("Height"), 320, 120, 1200);
                bool allowTransparency = ParseBool((string?)root.Attribute("AllowTransparency"), false);

                return new ThemeDefinition(bg, fg, name, accent, width, height, allowTransparency, xml, IsCustomLayout: false);
            }

            if (root.Name.LocalName.Equals("BloxstrapCustomBootstrapper", StringComparison.OrdinalIgnoreCase))
            {
                string name = (string?)root.Attribute("Name") ?? "CustomBootstrapper";
                var colors = root.Element("Colors");
                string bg = ValidateColor((string?)root.Attribute("Background") ?? (string?)colors?.Attribute("Background"), DefaultBackground);
                string fg = ValidateColor((string?)root.Attribute("Foreground") ?? (string?)colors?.Attribute("Foreground"), DefaultForeground);
                string accent = ValidateColor((string?)root.Attribute("Accent") ?? (string?)colors?.Attribute("Accent"), DefaultAccent);
                double width = ParseDouble((string?)root.Attribute("Width"), 500, 200, 1600);
                double height = ParseDouble((string?)root.Attribute("Height"), 320, 120, 1200);
                bool allowTransparency = ParseBool((string?)root.Attribute("AllowTransparency"), false);

                return new ThemeDefinition(bg, fg, name, accent, width, height, allowTransparency, xml, IsCustomLayout: true);
            }

            return GetDefaultFluent();
        }
        catch
        {
            return GetDefaultFluent();
        }
    }

    public static ThemeDefinition LoadFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return GetDefaultFluent();

        try
        {
            string xml = File.ReadAllText(filePath);
            return Parse(xml);
        }
        catch
        {
            return GetDefaultFluent();
        }
    }

    public static IReadOnlyList<string> ListAvailableThemes(string customThemesDir)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(customThemesDir) || !Directory.Exists(customThemesDir))
            return results;

        try
        {
            foreach (var file in Directory.GetFiles(customThemesDir, "*.xml", SearchOption.TopDirectoryOnly))
            {
                results.Add(Path.GetFileNameWithoutExtension(file));
            }

            foreach (var dir in Directory.GetDirectories(customThemesDir))
            {
                string themeXml = Path.Combine(dir, "Theme.xml");
                if (File.Exists(themeXml))
                {
                    results.Add(Path.GetFileName(dir));
                }
            }
        }
        catch { }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? ResolveThemeFilePath(string customThemesDir, string? themeName)
    {
        if (string.IsNullOrWhiteSpace(customThemesDir) || string.IsNullOrWhiteSpace(themeName))
            return null;

        string safeName = Path.GetFileName(themeName.Trim());
        string direct = Path.Combine(customThemesDir, safeName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? safeName : $"{safeName}.xml");
        if (File.Exists(direct)) return direct;

        string folderTheme = Path.Combine(customThemesDir, safeName, "Theme.xml");
        if (File.Exists(folderTheme)) return folderTheme;

        return null;
    }

    private static string ValidateColor(string? candidate, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return fallback;

        string trimmed = candidate.Trim();
        if (HexColorRegex().IsMatch(trimmed))
        {
            return trimmed.Length == 7 ? $"#FF{trimmed[1..]}" : trimmed;
        }

        return fallback;
    }

    private static double ParseDouble(string? raw, double fallback, double min, double max)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) && !double.IsNaN(val) && !double.IsInfinity(val))
        {
            return Math.Clamp(val, min, max);
        }
        return fallback;
    }

    private static bool ParseBool(string? raw, bool fallback)
    {
        if (bool.TryParse(raw, out bool val))
            return val;
        return fallback;
    }
}
