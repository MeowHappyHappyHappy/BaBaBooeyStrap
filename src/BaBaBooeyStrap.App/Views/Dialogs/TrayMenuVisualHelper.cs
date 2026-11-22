using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BaBaBooeyStrap.Core.Interop;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using TextBlock = System.Windows.Controls.TextBlock;

namespace BaBaBooeyStrap.App.Views.Dialogs;

internal sealed class TrayMenuPalette
{
    public bool IsLight { get; }
    public Brush CardBackground { get; }
    public Brush CardBorder { get; }
    public Brush HeaderBackground { get; }
    public Brush SeparatorBrush { get; }
    public Brush TextPrimary { get; }
    public Brush TextSecondary { get; }
    public Brush TextDisabled { get; }
    public Brush RowHoverBackground { get; }
    public Brush RowHoverBorder { get; }
    public Brush RowCheckedBackground { get; }
    public Brush RowCheckedBorder { get; }
    public Brush DangerForeground { get; }
    public Brush DangerHoverBackground { get; }
    public Brush DangerHoverBorder { get; }
    public Brush BadgeBackground { get; }
    public Brush VersionPillBackground { get; }
    public Brush AccentBrush { get; }
    public Brush FlagBorderBrush { get; }

    private TrayMenuPalette(bool isLight)
    {
        IsLight = isLight;
        if (isLight)
        {
            CardBackground = Freeze(Color.FromArgb(0xF8, 0xFA, 0xFA, 0xFC));
            CardBorder = Freeze(Color.FromArgb(0x24, 0x00, 0x00, 0x00));
            HeaderBackground = Freeze(Color.FromArgb(0x0D, 0x0F, 0x17, 0x2A));
            SeparatorBrush = Freeze(Color.FromArgb(0x18, 0x00, 0x00, 0x00));
            TextPrimary = Freeze(Color.FromRgb(0x18, 0x18, 0x1B));
            TextSecondary = Freeze(Color.FromRgb(0x52, 0x52, 0x5B));
            TextDisabled = Freeze(Color.FromArgb(0x70, 0x52, 0x52, 0x5B));
            RowHoverBackground = Freeze(Color.FromArgb(0x12, 0x00, 0x00, 0x00));
            RowHoverBorder = Freeze(Color.FromArgb(0x1E, 0x00, 0x00, 0x00));
            RowCheckedBackground = Freeze(Color.FromArgb(0x1C, 0x25, 0x63, 0xEB));
            RowCheckedBorder = Freeze(Color.FromArgb(0x55, 0x25, 0x63, 0xEB));
            DangerForeground = Freeze(Color.FromRgb(0xDC, 0x26, 0x26));
            DangerHoverBackground = Freeze(Color.FromArgb(0x1C, 0xEF, 0x44, 0x44));
            DangerHoverBorder = Freeze(Color.FromArgb(0x45, 0xEF, 0x44, 0x44));
            BadgeBackground = Freeze(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            VersionPillBackground = Freeze(Color.FromArgb(0x14, 0x25, 0x63, 0xEB));
            AccentBrush = Freeze(Color.FromRgb(0x25, 0x63, 0xEB));
            FlagBorderBrush = Freeze(Color.FromArgb(0x2A, 0x00, 0x00, 0x00));
        }
        else
        {
            CardBackground = Freeze(Color.FromArgb(0xF5, 0x1A, 0x1D, 0x24));
            CardBorder = Freeze(Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF));
            HeaderBackground = Freeze(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            SeparatorBrush = Freeze(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            TextPrimary = Freeze(Color.FromRgb(0xF8, 0xFA, 0xFC));
            TextSecondary = Freeze(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
            TextDisabled = Freeze(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            RowHoverBackground = Freeze(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
            RowHoverBorder = Freeze(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            RowCheckedBackground = Freeze(Color.FromArgb(0x2E, 0x3B, 0x82, 0xF6));
            RowCheckedBorder = Freeze(Color.FromArgb(0x70, 0x60, 0xA5, 0xFA));
            DangerForeground = Freeze(Color.FromRgb(0xFC, 0xA5, 0xA5));
            DangerHoverBackground = Freeze(Color.FromArgb(0x28, 0xEF, 0x44, 0x44));
            DangerHoverBorder = Freeze(Color.FromArgb(0x55, 0xF8, 0x71, 0x71));
            BadgeBackground = Freeze(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            VersionPillBackground = Freeze(Color.FromArgb(0x24, 0x60, 0xA5, 0xFA));
            AccentBrush = Freeze(Color.FromRgb(0x60, 0xA5, 0xFA));
            FlagBorderBrush = Freeze(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        }
    }

    public static TrayMenuPalette CreateCurrent()
    {
        bool isLight = false;
        try
        {
            isLight = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light;
        }
        catch { }
        return new TrayMenuPalette(isLight);
    }

    public static Brush GetStatusColor(TrayMultiInstanceState state) => state switch
    {
        TrayMultiInstanceState.Active => Freeze(Color.FromRgb(0x10, 0xB9, 0x81)),
        TrayMultiInstanceState.Blocked => Freeze(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        _ => Freeze(Color.FromRgb(0x71, 0x71, 0x7A))
    };

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

internal sealed class TrayMenuBranchController : IDisposable
{
    public const double ShadowMarginDip = 10.0;

    private readonly Window _ownerWindow;
    private readonly Panel _itemsHost;
    private readonly Func<User32.RECT> _getWorkArea;
    private readonly Func<nint, bool> _isChainWindow;
    private readonly Action _dismissAll;
    private readonly DispatcherTimer _hoverTimer;

    private TrayMenuPalette _palette;
    private TraySubMenuWindow? _activeChild;
    private TrayMenuItemModel? _pendingItem;
    private FrameworkElement? _pendingRow;
    private TrayMenuItemModel? _openParentItem;
    private bool _disposed;

    public TrayMenuBranchController(
        Window ownerWindow,
        Panel itemsHost,
        Func<User32.RECT> getWorkArea,
        Func<nint, bool> isChainWindow,
        Action dismissAll)
    {
        _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));
        _itemsHost = itemsHost ?? throw new ArgumentNullException(nameof(itemsHost));
        _getWorkArea = getWorkArea ?? throw new ArgumentNullException(nameof(getWorkArea));
        _isChainWindow = isChainWindow ?? throw new ArgumentNullException(nameof(isChainWindow));
        _dismissAll = dismissAll ?? throw new ArgumentNullException(nameof(dismissAll));
        _palette = TrayMenuPalette.CreateCurrent();

        _hoverTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(110)
        };
        _hoverTimer.Tick += OnHoverTimerTick;
    }

    public void PopulateItems(IReadOnlyList<TrayMenuItemModel> items, TrayMenuPalette palette)
    {
        _palette = palette;
        CloseChild();
        _itemsHost.Children.Clear();

        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                _itemsHost.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(6, 5, 6, 5),
                    Background = palette.SeparatorBrush
                });
                continue;
            }

            _itemsHost.Children.Add(BuildRowElement(item, palette));
        }
    }

    public bool ContainsHwnd(nint hwnd)
    {
        if (hwnd == nint.Zero) return false;
        nint selfHwnd = new WindowInteropHelper(_ownerWindow).Handle;
        if (selfHwnd == hwnd) return true;
        return _activeChild != null && _activeChild.IsVisible && _activeChild.ContainsHwnd(hwnd);
    }

    public void CloseChild()
    {
        _hoverTimer.Stop();
        _pendingItem = null;
        _pendingRow = null;
        _openParentItem = null;
        if (_activeChild != null)
        {
            _activeChild.HideBranch();
        }
    }

    public void ForceCloseChild()
    {
        _hoverTimer.Stop();
        if (_activeChild != null)
        {
            _activeChild.ForceClose();
            _activeChild = null;
        }
    }

    private Border BuildRowElement(TrayMenuItemModel item, TrayMenuPalette palette)
    {
        bool hasChildren = item.Children is { Count: > 0 };
        bool isInteractive = item.IsEnabled && (hasChildren || item.OnClick != null);

        var rowBorder = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 1, 0, 1),
            BorderThickness = new Thickness(1),
            Cursor = isInteractive ? Cursors.Hand : Cursors.Arrow,
            Background = item.IsChecked && !hasChildren ? palette.RowCheckedBackground : Brushes.Transparent,
            BorderBrush = item.IsChecked && !hasChildren ? palette.RowCheckedBorder : Brushes.Transparent,
            Opacity = item.IsEnabled ? 1.0 : 0.65
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left slot: flag, symbol icon, or checkmark
        UIElement? leftVisual = null;
        bool leftUsedForGraphic = false;

        if (item.FlagImage != null)
        {
            leftUsedForGraphic = true;
            var flagImg = new System.Windows.Controls.Image
            {
                Source = item.FlagImage,
                Stretch = Stretch.UniformToFill
            };
            RenderOptions.SetBitmapScalingMode(flagImg, BitmapScalingMode.HighQuality);
            leftVisual = new Border
            {
                Width = 18,
                Height = 13,
                CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(1),
                BorderBrush = palette.FlagBorderBrush,
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = flagImg
            };
        }
        else if (item.IconSymbol.HasValue)
        {
            leftUsedForGraphic = true;
            Brush iconFg = item.IsDanger
                ? palette.DangerForeground
                : (item.IsChecked ? palette.AccentBrush : (item.IsEnabled ? palette.TextSecondary : palette.TextDisabled));
            leftVisual = new SymbolIcon
            {
                Symbol = item.IconSymbol.Value,
                FontSize = 15,
                Foreground = iconFg,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }
        else if (item.IsChecked)
        {
            leftVisual = new SymbolIcon
            {
                Symbol = SymbolRegular.Checkmark16,
                FontSize = 14,
                Foreground = palette.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }

        if (leftVisual != null)
        {
            Grid.SetColumn(leftVisual, 0);
            grid.Children.Add(leftVisual);
        }

        // Center text (+ optional subtitle)
        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Brush primaryFg = item.IsDanger
            ? palette.DangerForeground
            : (item.IsEnabled ? palette.TextPrimary : palette.TextDisabled);
        var titleText = new TextBlock
        {
            Text = item.Text,
            FontSize = 12.5,
            FontWeight = item.IsChecked ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = primaryFg,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textStack.Children.Add(titleText);

        if (!string.IsNullOrWhiteSpace(item.Subtitle))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = item.Subtitle,
                FontSize = 10.5,
                Foreground = palette.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }

        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        // Right accessory stack: badge, right checkmark (if left graphic was taken), chevron
        var rightStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        if (!string.IsNullOrWhiteSpace(item.BadgeText))
        {
            rightStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 0, hasChildren || (item.IsChecked && leftUsedForGraphic) ? 6 : 0, 0),
                Background = palette.BadgeBackground,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = item.BadgeText,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = palette.TextSecondary
                }
            });
        }

        if (item.IsChecked && leftUsedForGraphic)
        {
            rightStack.Children.Add(new SymbolIcon
            {
                Symbol = SymbolRegular.Checkmark16,
                FontSize = 13,
                Foreground = palette.AccentBrush,
                Margin = new Thickness(0, 0, hasChildren ? 4 : 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        if (hasChildren)
        {
            rightStack.Children.Add(new SymbolIcon
            {
                Symbol = SymbolRegular.ChevronRight16,
                FontSize = 12,
                Foreground = palette.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        if (rightStack.Children.Count > 0)
        {
            Grid.SetColumn(rightStack, 2);
            grid.Children.Add(rightStack);
        }

        rowBorder.Child = grid;

        rowBorder.MouseEnter += (_, _) =>
        {
            if (item.IsEnabled)
            {
                rowBorder.Background = item.IsDanger
                    ? palette.DangerHoverBackground
                    : palette.RowHoverBackground;
                rowBorder.BorderBrush = item.IsDanger
                    ? palette.DangerHoverBorder
                    : palette.RowHoverBorder;
            }

            if (_disposed) return;

            if (hasChildren && ReferenceEquals(_openParentItem, item) && _activeChild is { IsVisible: true })
            {
                _hoverTimer.Stop();
                return;
            }

            _pendingItem = item;
            _pendingRow = rowBorder;
            _hoverTimer.Stop();
            _hoverTimer.Start();
        };

        rowBorder.MouseLeave += (_, _) =>
        {
            rowBorder.Background = item.IsChecked && !hasChildren ? palette.RowCheckedBackground : Brushes.Transparent;
            rowBorder.BorderBrush = item.IsChecked && !hasChildren ? palette.RowCheckedBorder : Brushes.Transparent;
        };

        rowBorder.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!item.IsEnabled) return;

            if (hasChildren)
            {
                _hoverTimer.Stop();
                OpenChildForRow(rowBorder, item);
                return;
            }

            if (item.OnClick != null)
            {
                var action = item.OnClick;
                var dispatcher = _ownerWindow.Dispatcher;
                _dismissAll();
                _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
            }
        };

        return rowBorder;
    }

    private void OnHoverTimerTick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        if (_disposed || !_ownerWindow.IsVisible) return;

        var item = _pendingItem;
        var row = _pendingRow;
        if (item == null || row == null || !row.IsMouseOver)
        {
            return;
        }

        if (item.Children is { Count: > 0 } && item.IsEnabled)
        {
            OpenChildForRow(row, item);
        }
        else
        {
            CloseChild();
        }
    }

    private void OpenChildForRow(FrameworkElement row, TrayMenuItemModel parentItem)
    {
        if (_disposed || parentItem.Children == null || parentItem.Children.Count == 0) return;

        nint parentHwnd = new WindowInteropHelper(_ownerWindow).Handle;
        if (parentHwnd == nint.Zero || !User32.GetWindowRect(parentHwnd, out var parentRect))
        {
            return;
        }

        var parentDpi = VisualTreeHelper.GetDpi(_ownerWindow);
        int parentPadX = (int)Math.Round(ShadowMarginDip * parentDpi.DpiScaleX);
        int parentCardLeft = parentRect.Left + parentPadX;
        int parentCardRight = parentRect.Right - parentPadX;

        Point rowScreenPos;
        try
        {
            rowScreenPos = row.PointToScreen(new Point(0, 0));
        }
        catch
        {
            return;
        }

        int itemTopPhys = (int)Math.Round(rowScreenPos.Y) - (int)Math.Round(6 * parentDpi.DpiScaleY);

        _activeChild ??= new TraySubMenuWindow(_getWorkArea, _isChainWindow, _dismissAll);
        _openParentItem = parentItem;
        _activeChild.ShowAtParentRow(
            parentItem.Children,
            _palette,
            parentCardLeft,
            parentCardRight,
            itemTopPhys,
            _getWorkArea());
    }

    public static void ApplyPaletteToCard(Border cardBorder, TrayMenuPalette palette)
    {
        cardBorder.Background = palette.CardBackground;
        cardBorder.BorderBrush = palette.CardBorder;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hoverTimer.Stop();
        ForceCloseChild();
    }
}
