using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.Bootstrapper.Mods.Presets;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;
using Microsoft.Win32;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class ModsPage : Page
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public ModsPage()
    {
        InitializeComponent();
        CursorCombo.ItemsSource = new[] { CursorPreset.Default, CursorPreset.Classic2006, CursorPreset.Classic2013 };
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        string dir = ViewModel?.AppPaths.Modifications ?? Paths.Current.Modifications;
        FluentDialogService.TryOpenFolderInExplorer(dir, Window.GetWindow(this));
    }

    private void ChooseFont_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        try
        {
            var owner = Window.GetWindow(this);
            var ofd = new OpenFileDialog { Filter = LocalizationService.Instance.GetString("Str_Mods_FontFileFilter") };
            if ((owner != null ? ofd.ShowDialog(owner) : ofd.ShowDialog()) == true)
            {
                ViewModel.Mods.ApplyCustomFont(ofd.FileName);
            }
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("ModsPage.ChooseFont_Click", ex);
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                Window.GetWindow(this));
        }
    }

    private void ResetFont_Click(object sender, RoutedEventArgs e) => RunSafe("ModsPage.ResetFont_Click", () => ViewModel?.Mods.ResetCustomFont());

    private void ChooseSound_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        try
        {
            var owner = Window.GetWindow(this);
            var ofd = new OpenFileDialog { Filter = LocalizationService.Instance.GetString("Str_Mods_OggFileFilter") };
            if ((owner != null ? ofd.ShowDialog(owner) : ofd.ShowDialog()) == true)
            {
                ViewModel.Mods.ApplyCustomDeathSound(ofd.FileName);
            }
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("ModsPage.ChooseSound_Click", ex);
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                Window.GetWindow(this));
        }
    }

    private void ResetSound_Click(object sender, RoutedEventArgs e) => RunSafe("ModsPage.ResetSound_Click", () => ViewModel?.Mods.ResetDeathSound());

    private void RunSafe(string context, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic(context, ex);
            FluentDialogService.ShowError(
                LocalizationService.Instance.GetString("Str_Dialog_ErrorTitle"),
                ex.Message,
                Window.GetWindow(this));
        }
    }
}
