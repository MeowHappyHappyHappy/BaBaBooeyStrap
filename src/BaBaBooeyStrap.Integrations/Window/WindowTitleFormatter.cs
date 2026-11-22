using System.Text.RegularExpressions;

namespace BaBaBooeyStrap.Integrations.Window;

public static partial class WindowTitleFormatter
{
    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\[\s*\]|\(\s*\)", RegexOptions.Compiled)]
    private static partial Regex EmptyBracketsRegex();

    [GeneratedRegex(@"(?<=[\]\)])\s{2,}-\s+", RegexOptions.Compiled)]
    private static partial Regex DanglingSeparatorAfterBracketRegex();

    [GeneratedRegex(@"^\s*-\s+|\s+-\s*$", RegexOptions.Compiled)]
    private static partial Regex EdgeSeparatorRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

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

        string formatted = PlaceholderRegex().Replace(template, match =>
        {
            string key = match.Groups[1].Value;
            return lookup.TryGetValue(key, out string? value) ? value : match.Value;
        });

        formatted = EmptyBracketsRegex().Replace(formatted, string.Empty);
        formatted = DanglingSeparatorAfterBracketRegex().Replace(formatted, " ");
        formatted = EdgeSeparatorRegex().Replace(formatted, string.Empty);
        formatted = MultiSpaceRegex().Replace(formatted, " ").Trim();

        return formatted;
    }
}
