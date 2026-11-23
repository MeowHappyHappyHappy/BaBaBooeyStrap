using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BaBaBooeyStrap.App.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

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
    private const int MaxCacheSize = 256;
    private static readonly ConcurrentDictionary<string, Brush> BrushCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Brush FallbackBrush = CreateFrozenBrush(Color.FromRgb(0x20, 0x20, 0x20));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            string key = hex.Trim();
            if (BrushCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(key);
                var brush = CreateFrozenBrush(color);
                if (BrushCache.Count >= MaxCacheSize)
                {
                    BrushCache.Clear();
                }

                BrushCache.TryAdd(key, brush);
                return brush;
            }
            catch
            {
            }
        }

        return FallbackBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
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

public sealed class NullableDoubleToIntConverter : IValueConverter
{
    public static NullableDoubleToIntConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            int i => (double)i,
            long l => (double)l,
            double d => d,
            float f => (double)f,
            _ => 0d
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
        {
            return Binding.DoNothing;
        }

        if (value is double d && double.IsFinite(d))
        {
            return (int)Math.Round(d);
        }

        if (value is string s && double.TryParse(s, NumberStyles.Float, culture, out double parsed) && double.IsFinite(parsed))
        {
            return (int)Math.Round(parsed);
        }

        return Binding.DoNothing;
    }
}

public sealed class NullableDoubleToFloatConverter : IValueConverter
{
    public static NullableDoubleToFloatConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            float f => (double)f,
            double d => d,
            int i => (double)i,
            _ => 0d
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
        {
            return Binding.DoNothing;
        }

        if (value is double d && double.IsFinite(d))
        {
            return (float)d;
        }

        if (value is string s && float.TryParse(s, NumberStyles.Float, culture, out float parsed) && float.IsFinite(parsed))
        {
            return parsed;
        }

        return Binding.DoNothing;
    }
}

public static class NumberBoxGuard
{
    public static readonly DependencyProperty RestoreOnEmptyProperty =
        DependencyProperty.RegisterAttached(
            "RestoreOnEmpty",
            typeof(bool),
            typeof(NumberBoxGuard),
            new PropertyMetadata(false, OnRestoreOnEmptyChanged));

    public static bool GetRestoreOnEmpty(DependencyObject obj) => (bool)obj.GetValue(RestoreOnEmptyProperty);
    public static void SetRestoreOnEmpty(DependencyObject obj, bool value) => obj.SetValue(RestoreOnEmptyProperty, value);

    private static void OnRestoreOnEmptyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Wpf.Ui.Controls.NumberBox numberBox)
        {
            return;
        }

        numberBox.LostFocus -= OnNumberBoxLostFocus;
        if (e.NewValue is true)
        {
            numberBox.LostFocus += OnNumberBoxLostFocus;
        }
    }

    private static void OnNumberBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.NumberBox numberBox && numberBox.Value == null)
        {
            BindingOperations.GetBindingExpression(numberBox, Wpf.Ui.Controls.NumberBox.ValueProperty)?.UpdateTarget();
        }
    }
}
