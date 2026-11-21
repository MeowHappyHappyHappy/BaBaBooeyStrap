using BaBaBooeyStrap.App.Services;

namespace BaBaBooeyStrap.App.ViewModels.Sections;

internal static class LocalizedFormatHelper
{
    public static string Format(string key, params object?[] args)
    {
        if (args == null || args.Length == 0)
            return LocalizationService.Instance.GetString(key);

        object?[] resolved = new object?[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            resolved[i] = args[i] is Enum e
                ? LocalizationService.Instance.GetEnumDisplay(e)
                : args[i];
        }

        return LocalizationService.Instance.Format(key, resolved);
    }
}
