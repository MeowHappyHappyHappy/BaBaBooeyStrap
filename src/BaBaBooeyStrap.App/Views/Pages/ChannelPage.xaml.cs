using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.ViewModels;

namespace BaBaBooeyStrap.App.Views.Pages;

public partial class ChannelPage : Page
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public ChannelPage()
    {
        InitializeComponent();
    }

    private async void TestCdn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        await ViewModel.TestFastestCdnMirrorAsync();
    }
}
