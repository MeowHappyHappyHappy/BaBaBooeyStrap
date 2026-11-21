using System.Windows;
using BaBaBooeyStrap.App.Views.Dialogs;

namespace BaBaBooeyStrap.App.Services;

public static class FluentDialogService
{
    public static void ShowInfo(string title, string message, Window? owner = null) =>
        ShowSingleButton(title, message, FluentDialogSeverity.Info, owner);

    public static void ShowWarning(string title, string message, Window? owner = null) =>
        ShowSingleButton(title, message, FluentDialogSeverity.Warning, owner);

    public static void ShowError(string title, string message, Window? owner = null) =>
        ShowSingleButton(title, message, FluentDialogSeverity.Error, owner);

    public static bool ShowConfirm(
        string title,
        string message,
        string? confirmText = null,
        string? cancelText = null,
        bool isDestructive = false,
        Window? owner = null)
    {
        var loc = LocalizationService.Instance;
        string primary = confirmText ?? loc.GetString("Str_Common_Yes");
        string cancel = cancelText ?? loc.GetString("Str_Common_Cancel");
        var severity = isDestructive ? FluentDialogSeverity.Warning : FluentDialogSeverity.Question;

        var result = ShowCore(title, message, severity, primary, null, cancel, isDestructive, owner);
        return result == FluentDialogResult.Primary;
    }

    public static FluentDialogResult ShowSaveDiscardCancel(string title, string message, Window? owner = null)
    {
        var loc = LocalizationService.Instance;
        return ShowCore(
            title,
            message,
            FluentDialogSeverity.Question,
            loc.GetString("Str_Common_Save"),
            loc.GetString("Str_Common_Discard"),
            loc.GetString("Str_Common_Cancel"),
            isDestructive: false,
            owner: owner);
    }

    private static void ShowSingleButton(string title, string message, FluentDialogSeverity severity, Window? owner)
    {
        string okText = LocalizationService.Instance.GetString("Str_Common_Ok");
        ShowCore(title, message, severity, okText, null, null, isDestructive: false, owner: owner);
    }

    private static FluentDialogResult ShowCore(
        string title,
        string message,
        FluentDialogSeverity severity,
        string primaryText,
        string? secondaryText,
        string? cancelText,
        bool isDestructive,
        Window? owner)
    {
        var app = Application.Current;
        if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
        {
            return app.Dispatcher.Invoke(() =>
                ShowOnUiThread(title, message, severity, primaryText, secondaryText, cancelText, isDestructive, owner));
        }

        return ShowOnUiThread(title, message, severity, primaryText, secondaryText, cancelText, isDestructive, owner);
    }

    private static FluentDialogResult ShowOnUiThread(
        string title,
        string message,
        FluentDialogSeverity severity,
        string primaryText,
        string? secondaryText,
        string? cancelText,
        bool isDestructive,
        Window? explicitOwner)
    {
        var resolvedOwner = ResolveOwner(explicitOwner);
        var dialog = new FluentMessageDialog(
            title,
            message,
            severity,
            primaryText,
            secondaryText,
            cancelText,
            isDestructive,
            resolvedOwner);

        dialog.ShowDialog();
        return dialog.Result;
    }

    private static Window? ResolveOwner(Window? explicitOwner)
    {
        if (explicitOwner != null && explicitOwner.IsLoaded && explicitOwner.IsVisible)
            return explicitOwner;

        var windows = Application.Current?.Windows;
        if (windows == null) return null;

        Window? fallback = null;
        foreach (Window win in windows)
        {
            if (win is FluentMessageDialog || !win.IsLoaded || !win.IsVisible)
                continue;

            if (win.IsActive)
                return win;

            fallback ??= win;
        }

        return fallback;
    }
}
