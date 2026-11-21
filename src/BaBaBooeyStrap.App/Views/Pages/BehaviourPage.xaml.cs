using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.ViewModels;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class BehaviourPage : Page
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public BehaviourPage()
    {
        InitializeComponent();
    }

    private void CleanNow_Click(object sender, RoutedEventArgs e) => ViewModel?.WindowIntegrations.RunCleanerNow();
}
