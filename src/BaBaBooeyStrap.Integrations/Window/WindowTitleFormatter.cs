using System.Text.RegularExpressions;

namespace BaBaBooeyStrap.Integrations.Window;

public static partial class WindowTitleFormatter
{
    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    public static string Format(string? template, IReadOnlyDictionary<string, string>? placeholders)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        if (placeholders == null || placeholders.Count == 0)
        {
            return template;
        }

        var lookup = new Dictionary<string, string>(placeholders, StringComparer.OrdinalIgnoreCase);

        return PlaceholderRegex().Replace(template, match =>
        {
            string key = match.Groups[1].Value;
            return lookup.TryGetValue(key, out string? value) ? value : match.Value;
        });
    }
}
