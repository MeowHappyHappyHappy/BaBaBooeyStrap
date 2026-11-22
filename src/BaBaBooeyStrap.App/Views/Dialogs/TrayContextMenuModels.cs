using System.Windows;
using System.Windows.Media;
using BaBaBooeyStrap.Core.Interop;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public enum TrayMultiInstanceState
{
    Active,
    Blocked,
    Off
}

public sealed record TrayMenuItemModel(
    string Text,
    string? Subtitle = null,
    SymbolRegular? IconSymbol = null,
    ImageSource? FlagImage = null,
    string? BadgeText = null,
    bool IsChecked = false,
    bool IsEnabled = true,
    bool IsDanger = false,
    bool IsSeparator = false,
    Action? OnClick = null,
    IReadOnlyList<TrayMenuItemModel>? Children = null)
{
    public static TrayMenuItemModel Separator() => new(string.Empty, IsSeparator: true, IsEnabled: false);
}

public sealed record TrayMenuSnapshot(
    string AppTitle,
    string VersionText,
    string MultiInstanceBadgeText,
    TrayMultiInstanceState MultiInstanceBadgeState,
    int RunningCount,
    IReadOnlyList<TrayMenuItemModel> Items);

internal static class TrayMenuPositioner
{
    private const int ScreenMarginPx = 6;
    private const int SubMenuOverlapPx = 4;

    public static (int Left, int Top) ComputePopupPosition(
        int cursorX,
        int cursorY,
        int menuWidth,
        int menuHeight,
        User32.RECT workArea)
    {
        int safeWidth = Math.Max(1, menuWidth);
        int safeHeight = Math.Max(1, menuHeight);

        int minLeft = workArea.Left + ScreenMarginPx;
        int maxLeft = Math.Max(minLeft, workArea.Right - ScreenMarginPx - safeWidth);
        int minTop = workArea.Top + ScreenMarginPx;
        int maxTop = Math.Max(minTop, workArea.Bottom - ScreenMarginPx - safeHeight);

        int left = cursorX + safeWidth > workArea.Right - ScreenMarginPx
            ? cursorX - safeWidth
            : cursorX;
        left = Math.Clamp(left, minLeft, maxLeft);

        int workMidY = workArea.Top + (workArea.Height / 2);
        bool openUpward = cursorY >= workMidY || cursorY + safeHeight > workArea.Bottom - ScreenMarginPx;
        int top = openUpward
            ? cursorY - safeHeight
            : cursorY;
        top = Math.Clamp(top, minTop, maxTop);

        return (left, top);
    }

    public static (int Left, int Top) ComputeSubMenuPosition(
        int parentLeft,
        int parentRight,
        int itemTop,
        int subWidth,
        int subHeight,
        User32.RECT workArea)
    {
        int safeWidth = Math.Max(1, subWidth);
        int safeHeight = Math.Max(1, subHeight);

        int minLeft = workArea.Left + ScreenMarginPx;
        int maxLeft = Math.Max(minLeft, workArea.Right - ScreenMarginPx - safeWidth);
        int minTop = workArea.Top + ScreenMarginPx;
        int maxTop = Math.Max(minTop, workArea.Bottom - ScreenMarginPx - safeHeight);

        int rightSideLeft = parentRight - SubMenuOverlapPx;
        int left;
        if (rightSideLeft + safeWidth <= workArea.Right - ScreenMarginPx)
        {
            left = rightSideLeft;
        }
        else
        {
            int leftSideLeft = parentLeft - safeWidth + SubMenuOverlapPx;
            left = Math.Clamp(leftSideLeft, minLeft, maxLeft);
        }

        int top = Math.Clamp(itemTop, minTop, maxTop);
        return (left, top);
    }
}
