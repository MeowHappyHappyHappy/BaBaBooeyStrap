using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.Bootstrapper.Mods.Presets;
using BaBaBooeyStrap.Core;
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
        Directory.CreateDirectory(Paths.Modifications);
        Process.Start(new ProcessStartInfo("explorer.exe", Paths.Modifications) { UseShellExecute = true });
    }

    private void ChooseFont_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var ofd = new OpenFileDialog { Filter = LocalizationService.Instance.GetString("Str_Mods_FontFileFilter") };
        if (ofd.ShowDialog() == true)
        {
            ViewModel.Mods.ApplyCustomFont(ofd.FileName);
        }
    }

    private void ResetFont_Click(object sender, RoutedEventArgs e) => ViewModel?.Mods.ResetCustomFont();

    private void ChooseSound_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var ofd = new OpenFileDialog { Filter = LocalizationService.Instance.GetString("Str_Mods_OggFileFilter") };
        if (ofd.ShowDialog() == true)
        {
            ViewModel.Mods.ApplyCustomDeathSound(ofd.FileName);
        }
    }

    private void ResetSound_Click(object sender, RoutedEventArgs e) => ViewModel?.Mods.ResetDeathSound();
}
