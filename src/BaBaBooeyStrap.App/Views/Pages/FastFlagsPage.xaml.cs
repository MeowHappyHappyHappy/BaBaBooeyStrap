using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.Views.Dialogs;
using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class FastFlagsPage : Page
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public FastFlagsPage()
    {
        InitializeComponent();
        RenderingCombo.ItemsSource = Enum.GetValues<RenderingMode>();
        MsaaCombo.ItemsSource = Enum.GetValues<MSAAMode>();
    }

    private void OpenEditor_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var editorVm = ViewModel.FastFlags.CreateFastFlagEditorViewModel();
        var dlg = new FastFlagEditorWindow(editorVm) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            ViewModel.FastFlags.LoadFastFlagPresets();
        }
    }

    private void ApplyPresets_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        bool applied = ViewModel.FastFlags.ApplyFastFlagPresets();
        ViewModel.SetLocalizedStatus(applied
            ? "Str_FastFlags_StatusPresetsApplied"
            : "Str_FastFlags_StatusManagerDisabled");
    }
}
