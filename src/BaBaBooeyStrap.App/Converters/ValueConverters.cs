using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BaBaBooeyStrap.App.Services;

namespace BaBaBooeyStrap.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value is Visibility v && v == Visibility.Visible;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible;
    }
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : false;
}

public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasContent = value is string s ? !string.IsNullOrWhiteSpace(s) : value != null;
        if (parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            hasContent = !hasContent;
        return hasContent ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class HexStringToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                return new SolidColorBrush(color);
            }
            catch { }
        }
        return new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class LocalizedEnumMultiConverter : IMultiValueConverter
{
    public static LocalizedEnumMultiConverter Instance { get; } = new();

    public object Convert(object?[]? values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values == null || values.Length == 0 || values[0] == null || values[0] == DependencyProperty.UnsetValue)
            return string.Empty;

        return LocalizationService.Instance.GetEnumDisplay(values[0]);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        Array.Empty<object>();
}

public sealed class LocalizedFormatMultiConverter : IMultiValueConverter
{
    public static LocalizedFormatMultiConverter Instance { get; } = new();

    public object Convert(object?[]? values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string key || string.IsNullOrWhiteSpace(key))
            return string.Empty;

        if (values == null || values.Length <= 1)
            return LocalizationService.Instance.GetString(key);

        object?[] args = new object?[values.Length - 1];
        for (int i = 1; i < values.Length; i++)
        {
            args[i - 1] = values[i] == DependencyProperty.UnsetValue ? string.Empty : values[i];
        }

        return LocalizationService.Instance.Format(key, args);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        Array.Empty<object>();
}
