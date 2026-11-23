using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BaBaBooeyStrap.Core.Models.Enums;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace BaBaBooeyStrap.App.Services;

public static class ThemeService
{
    private static readonly Brush WarningForegroundDark = Freeze(0xFF, 0xF5, 0x9E, 0x0B);
    private static readonly Brush WarningBackgroundDark = Freeze(0x33, 0xF5, 0x9E, 0x0B);
    private static readonly Brush WarningBorderDark = Freeze(0xFF, 0xF5, 0x9E, 0x0B);

    private static readonly Brush WarningForegroundLight = Freeze(0xFF, 0x92, 0x40, 0x0E);
    private static readonly Brush WarningBackgroundLight = Freeze(0x1F, 0xD9, 0x77, 0x06);
    private static readonly Brush WarningBorderLight = Freeze(0xFF, 0xD9, 0x77, 0x06);
    public static ApplicationTheme ResolveApplicationTheme(Theme theme, SystemTheme? systemThemeOverride = null)
    {
        return theme switch
        {
            Theme.Light => ApplicationTheme.Light,
            Theme.Dark => ApplicationTheme.Dark,
            _ => ResolveFromSystemTheme(systemThemeOverride ?? GetCurrentSystemTheme())
        };
    }

    private static SystemTheme GetCurrentSystemTheme()
    {
        try
        {
            SystemThemeManager.UpdateSystemThemeCache();
            return ApplicationThemeManager.GetSystemTheme();
        }
        catch
        {
            return SystemTheme.Dark;
        }
    }

    private static ApplicationTheme ResolveFromSystemTheme(SystemTheme systemTheme)
    {
        return systemTheme switch
        {
            SystemTheme.Light or SystemTheme.HCWhite or SystemTheme.Sunrise or SystemTheme.Flow => ApplicationTheme.Light,
            _ => ApplicationTheme.Dark
        };
    }

    public static void ApplyTheme(Theme theme, WindowBackdropType backdrop, Window? watchedWindow = null)
    {
        ApplicationTheme appTheme = ResolveApplicationTheme(theme);
        try
        {
            ApplicationThemeManager.Apply(appTheme, backdrop, true);
        }
        catch
        {
        }

        ApplyWarningBrushes(appTheme == ApplicationTheme.Light);

        if (watchedWindow != null && new WindowInteropHelper(watchedWindow).Handle != IntPtr.Zero)
        {
            UnwatchWindow(watchedWindow);
            if (theme == Theme.Default)
            {
                try
                {
                    SystemThemeWatcher.Watch(watchedWindow, backdrop, true);
                }
                catch
                {
                }
            }
        }
    }

    private static void ApplyWarningBrushes(bool isLight)
    {
        try
        {
            var app = Application.Current;
            if (app?.Resources == null)
            {
                return;
            }

            if (app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                return;
            }

            app.Resources["AppWarningForegroundBrush"] = isLight ? WarningForegroundLight : WarningForegroundDark;
            app.Resources["AppWarningBackgroundBrush"] = isLight ? WarningBackgroundLight : WarningBackgroundDark;
            app.Resources["AppWarningBorderBrush"] = isLight ? WarningBorderLight : WarningBorderDark;
        }
        catch
        {
        }
    }

    private static Brush Freeze(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static void UnwatchWindow(Window? window)
    {
        if (window == null) return;

        try
        {
            SystemThemeWatcher.UnWatch(window);
        }
        catch
        {
        }
    }
}
