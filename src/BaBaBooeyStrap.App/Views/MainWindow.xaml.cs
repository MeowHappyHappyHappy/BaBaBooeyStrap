using System.ComponentModel;
using System.Windows;
using System.Windows.Documents;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.Views.Pages;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly IJsonManager<Settings> _settingsManager;
    private readonly IJsonManager<State> _stateManager;
    private readonly NotifyIconWrapper? _trayWrapper;
    private readonly Action? _onLaunchRequested;
    private bool _allowRealClose;
    private bool _shownTrayBalloon;
    private bool _initialNavigated;

    public MainWindowViewModel ViewModel { get; }

    public MainWindow(
        MainWindowViewModel viewModel,
        IJsonManager<Settings> settingsManager,
        IJsonManager<State> stateManager,
        NotifyIconWrapper? trayWrapper = null,
        Action? onLaunchRequested = null)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        _trayWrapper = trayWrapper;
        _onLaunchRequested = onLaunchRequested;

        DataContext = ViewModel;
        InitializeComponent();

        RestoreWindowBounds();
        ApplyTheme(ViewModel.WindowIntegrations.CurrentTheme);
        UpdateBackdrop();

        ViewModel.WindowIntegrations.ThemeChanged += (_, theme) => ApplyTheme(theme);
        ViewModel.WindowIntegrations.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModels.Sections.WindowAndIntegrationsViewModel.UseAcrylicBackground))
            {
                UpdateBackdrop();
                ApplyTheme(ViewModel.WindowIntegrations.CurrentTheme);
            }
        };

        Loaded += (_, _) =>
        {
            ApplyTheme(ViewModel.WindowIntegrations.CurrentTheme);
            _trayWrapper?.AttachWindow(this);
            if (!_initialNavigated)
            {
                _initialNavigated = true;
                RootNavigation.Navigate(typeof(AccountsPage));
            }
        };

        StateChanged += (_, _) =>
        {
            if (WindowState == System.Windows.WindowState.Minimized &&
                ViewModel.WindowIntegrations.MinimizeToTray &&
                _trayWrapper != null)
            {
                Hide();
                _trayWrapper.MinimizeToTray();
            }
        };

        Closed += (_, _) =>
        {
            ThemeService.UnwatchWindow(this);
            ViewModel.DetachLocalization();
        };
    }

    public void ForceClose()
    {
        _allowRealClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowRealClose && _trayWrapper != null && ViewModel.WindowIntegrations.MinimizeToTray)
        {
            PersistWindowBounds();
            ViewModel.SaveSettings(_settingsManager);
            e.Cancel = true;
            Hide();
            _trayWrapper.MinimizeToTray();
            if (!_shownTrayBalloon)
            {
                _shownTrayBalloon = true;
                _trayWrapper.ShowBalloonNotification(
                    LocalizationService.Instance.GetString("Str_Tray_MinimizedTitle"),
                    LocalizationService.Instance.GetString("Str_Tray_MinimizedMessage"));
            }
            return;
        }

        if (!_allowRealClose && ViewModel.HasUnsavedChanges)
        {
            var choice = FluentDialogService.ShowSaveDiscardCancel(
                LocalizationService.Instance.GetString("Str_Dialog_UnsavedTitle"),
                LocalizationService.Instance.GetString("Str_Dialog_UnsavedMessage"),
                owner: this);

            if (choice == Dialogs.FluentDialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (choice == Dialogs.FluentDialogResult.Primary)
            {
                ViewModel.SaveSettings(_settingsManager);
            }
        }

        PersistWindowBounds();
        if (!_allowRealClose && !ViewModel.HasUnsavedChanges && _stateManager != null)
        {
            try
            {
                ViewModel.AccountManagement.PersistLastTargetState();
                _stateManager.Save();
            }
            catch { }
        }

        ViewModel.AccountManagement.CancelActiveLaunch();
        base.OnClosing(e);
    }

    private void RootNavigation_Navigated(NavigationView sender, NavigatedEventArgs args)
    {
        if (args.Page is FrameworkElement fe)
        {
            fe.DataContext = ViewModel;
        }

        if (args.Page is System.Windows.Controls.Page page)
        {
            page.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextFillColorPrimaryBrush");
            page.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveSettings(_settingsManager);
    }

    private void SaveAndLaunch_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveSettings(_settingsManager);
        _onLaunchRequested?.Invoke();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RestoreWindowBounds()
    {
        var clamped = MainWindowViewModel.ClampWindowBounds(
            _stateManager.Prop.SettingsWindow,
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        Width = clamped.Width;
        Height = clamped.Height;
        Left = clamped.Left;
        Top = clamped.Top;
    }

    private void PersistWindowBounds()
    {
        if (WindowState == System.Windows.WindowState.Normal)
        {
            _stateManager.Prop.SettingsWindow = new Core.Models.WindowState
            {
                Width = Width,
                Height = Height,
                Left = Left,
                Top = Top
            };
        }
    }

    private void ApplyTheme(Theme theme)
    {
        ThemeService.ApplyTheme(theme, UpdateBackdropType(), this);
    }

    private void UpdateBackdrop()
    {
        WindowBackdropType = UpdateBackdropType();
    }

    private WindowBackdropType UpdateBackdropType() =>
        ViewModel.WindowIntegrations.UseAcrylicBackground ? WindowBackdropType.Acrylic : WindowBackdropType.Mica;
}
