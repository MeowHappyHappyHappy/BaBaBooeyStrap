using System.Windows;
using BaBaBooeyStrap.App.Models;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.Views.Dialogs;
using BaBaBooeyStrap.App.Views.Dialogs.Bootstrapper;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.App.Services;

public static class BootstrapperDialogFactory
{
    public static BootstrapperStyle ResolveStyle(BootstrapperStyle requestedStyle) =>
        Enum.IsDefined(requestedStyle) ? requestedStyle : BootstrapperStyle.FluentDialog;

    public static IBootstrapperDialog Create(
        Settings settings,
        CancellationTokenSource? cts = null,
        IPaths? paths = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var effectivePaths = paths ?? Paths.Current;
        var style = ResolveStyle(settings.BootstrapperStyle);

        ThemeDefinition theme = XmlThemeParser.GetDefaultFluent();
        if (style == BootstrapperStyle.CustomDialog)
        {
            string? themePath = XmlThemeParser.ResolveThemeFilePath(effectivePaths.CustomThemes, settings.SelectedCustomTheme);
            if (themePath != null)
            {
                theme = XmlThemeParser.LoadFromFile(themePath);
            }
        }

        var vm = new BootstrapperDialogViewModel(cts, Application.Current?.Dispatcher)
        {
            Title = string.IsNullOrWhiteSpace(settings.BootstrapperTitle) ? "BaBaBooeyStrap" : settings.BootstrapperTitle,
            Style = style,
            ThemeColors = theme,
            IconSource = IconResolver.ResolveImageSource(settings.BootstrapperIcon, settings.BootstrapperIconCustomLocation)
        };

        return style switch
        {
            BootstrapperStyle.FluentAeroDialog => new FluentDialog(vm, useAeroBackdrop: true),
            BootstrapperStyle.FluentDialog => new FluentDialog(vm, useAeroBackdrop: false),
            BootstrapperStyle.ClassicFluentDialog => new ClassicFluentDialog(vm),
            BootstrapperStyle.ByfronDialog => new ByfronDialog(vm),
            BootstrapperStyle.TerminalDialog => new TerminalDialog(vm),
            BootstrapperStyle.TwentyFiveDialog => new TwentyFiveDialog(vm),
            BootstrapperStyle.CustomDialog => new CustomDialog(vm),
            BootstrapperStyle.VistaDialog => new LegacyProgressDialog(vm, BootstrapperStyle.VistaDialog),
            BootstrapperStyle.LegacyDialog2008 => new LegacyProgressDialog(vm, BootstrapperStyle.LegacyDialog2008),
            BootstrapperStyle.LegacyDialog2011 => new LegacyProgressDialog(vm, BootstrapperStyle.LegacyDialog2011),
            BootstrapperStyle.ProgressDialog => new LegacyProgressDialog(vm, BootstrapperStyle.ProgressDialog),
            _ => new FluentDialog(vm, useAeroBackdrop: false)
        };
    }
}
