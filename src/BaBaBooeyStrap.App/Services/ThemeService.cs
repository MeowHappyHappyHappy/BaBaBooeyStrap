using System.Windows;
using System.Windows.Interop;
using BaBaBooeyStrap.Core.Models.Enums;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Services;

public static class ThemeService
{
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
        SystemThemeManager.UpdateSystemThemeCache();
        return ApplicationThemeManager.GetSystemTheme();
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
        ApplicationThemeManager.Apply(appTheme, backdrop, true);

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
