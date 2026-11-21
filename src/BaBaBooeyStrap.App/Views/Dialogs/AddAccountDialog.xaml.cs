using System.Windows;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Roblox;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class AddAccountDialog : FluentWindow
{
    public RobloxAccount? CreatedAccount { get; private set; }

    public AddAccountDialog()
    {
        InitializeComponent();
    }

    private async void VerifyAdd_Click(object sender, RoutedEventArgs e)
    {
        string cookie = CookieBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(cookie))
        {
            StatusText.Text = LocalizationService.Instance.GetString("Str_AddAccount_EmptyCookieError");
            return;
        }

        VerifyAddButton.IsEnabled = false;
        StatusText.Text = LocalizationService.Instance.GetString("Str_AddAccount_VerifyingStatus");

        try
        {
            using var auth = new RobloxAuthService();
            var user = await auth.GetAuthenticatedUserAsync(cookie);
            CreatedAccount = new RobloxAccount
            {
                UserId = user.Id,
                Username = user.Name,
                DisplayName = user.DisplayName,
                Cookie = cookie,
                Notes = NotesBox.Text.Trim(),
                CreatedAt = DateTimeOffset.UtcNow
            };

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = LocalizationService.Instance.Format("Str_AddAccount_FailedFormat", ex.Message);
            VerifyAddButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
