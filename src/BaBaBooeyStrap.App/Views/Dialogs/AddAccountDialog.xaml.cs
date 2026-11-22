using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Roblox;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public partial class AddAccountDialog : FluentWindow
{
    private const string RobloxLoginUrl = "https://www.roblox.com/login";
    private const string RobloxRootUrl = "https://www.roblox.com";
    private const string WebView2DownloadUrl = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/";

    private readonly bool _isUpdateMode;
    private string? _webViewUserDataFolder;
    private DispatcherTimer? _cookiePollTimer;
    private bool _webViewInitialized;
    private bool _webViewInitAttempted;
    private bool _isVerifyingWebCookie;
    private bool _dialogClosed;
    private string? _lastRejectedCookie;

    public RobloxAccount? CreatedAccount { get; private set; }

    public AddAccountDialog(string? existingUsername = null, string? existingNotes = null)
    {
        _isUpdateMode = !string.IsNullOrWhiteSpace(existingUsername);

        InitializeComponent();

        if (_isUpdateMode)
        {
            string updateTitle = LocalizationService.Instance.Format("Str_AddAccount_UpdateTitleFormat", existingUsername!);
            Title = updateTitle;
            DialogTitleBar.Title = updateTitle;
            LoginTabControl.SelectedItem = WebLoginTab;
            StatusText.Text = LocalizationService.Instance.GetString("Str_AddAccount_WebInitialStatus");
        }
        else
        {
            LoginTabControl.SelectedItem = WebLoginTab;
            StatusText.Text = LocalizationService.Instance.GetString("Str_AddAccount_WebInitialStatus");
        }

        if (!string.IsNullOrWhiteSpace(existingNotes))
        {
            NotesBox.Text = existingNotes;
        }

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateFooterForSelectedTab();
        if (LoginTabControl.SelectedItem == ManualCookieTab)
        {
            CookieBox.Focus();
        }
        else
        {
            await EnsureWebViewInitializedAsync();
        }
    }

    private async void LoginTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.Source, LoginTabControl))
        {
            return;
        }

        UpdateFooterForSelectedTab();

        if (LoginTabControl.SelectedItem == WebLoginTab)
        {
            StatusText.Text = LocalizationService.Instance.GetString("Str_AddAccount_WebInitialStatus");
            await EnsureWebViewInitializedAsync();
        }
        else if (LoginTabControl.SelectedItem == ManualCookieTab)
        {
            StatusText.Text = LocalizationService.Instance.GetString("Str_AddAccount_InitialStatus");
            CookieBox.Focus();
        }
    }

    private void UpdateFooterForSelectedTab()
    {
        bool manualSelected = LoginTabControl.SelectedItem == ManualCookieTab;
        VerifyAddButton.Visibility = manualSelected ? Visibility.Visible : Visibility.Collapsed;
        VerifyAddButton.IsDefault = manualSelected;
    }

    private async Task EnsureWebViewInitializedAsync()
    {
        if (_webViewInitAttempted || _dialogClosed)
        {
            return;
        }

        _webViewInitAttempted = true;
        WebLoginLoadingOverlay.Visibility = Visibility.Visible;
        WebViewFallbackPanel.Visibility = Visibility.Collapsed;

        try
        {
            _webViewUserDataFolder = Path.Combine(
                Paths.Current.Temp,
                "BaBaBooeyStrap",
                "WebViewLogin",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_webViewUserDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(null, _webViewUserDataFolder);
            if (_dialogClosed) return;

            await LoginWebView.EnsureCoreWebView2Async(environment);
            if (_dialogClosed) return;

            _webViewInitialized = true;
            LoginWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            LoginWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            LoginWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;

            LoginWebView.Source = new Uri(RobloxLoginUrl);

            _cookiePollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _cookiePollTimer.Tick += async (_, _) => await TryCaptureRobloxCookieAsync();
            _cookiePollTimer.Start();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowWebViewFallback(LocalizationService.Instance.GetString("Str_AddAccount_WebViewMissingDesc"));
        }
        catch (Exception ex)
        {
            ShowWebViewFallback(LocalizationService.Instance.Format("Str_AddAccount_WebViewInitFailedFormat", ex.Message));
        }
    }

    private void ShowWebViewFallback(string detailText)
    {
        if (_dialogClosed) return;
        WebLoginLoadingOverlay.Visibility = Visibility.Collapsed;
        WebViewFallbackDetailText.Text = detailText;
        WebViewFallbackPanel.Visibility = Visibility.Visible;
        StatusText.Text = detailText;
    }

    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        WebLoginLoadingOverlay.Visibility = Visibility.Collapsed;
        await TryCaptureRobloxCookieAsync();
    }

    private async Task TryCaptureRobloxCookieAsync()
    {
        if (_dialogClosed || !_webViewInitialized || _isVerifyingWebCookie || LoginWebView.CoreWebView2 == null)
        {
            return;
        }

        try
        {
            var cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync(RobloxRootUrl);
            var securityCookie = cookies.FirstOrDefault(c =>
                string.Equals(c.Name, ".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(c.Value));

            if (securityCookie == null)
            {
                return;
            }

            string cookieValue = securityCookie.Value.Trim();
            if (string.Equals(cookieValue, _lastRejectedCookie, StringComparison.Ordinal))
            {
                return;
            }

            _isVerifyingWebCookie = true;
            _cookiePollTimer?.Stop();

            await VerifyAndCompleteAsync(cookieValue, isFromWebView: true);
        }
        catch
        {
            _isVerifyingWebCookie = false;
        }
    }

    private async void VerifyAdd_Click(object sender, RoutedEventArgs e)
    {
        string cookie = RobloxAuthService.NormalizeCookie(CookieBox.Password);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            StatusText.Text = LocalizationService.Instance.GetString("Str_AddAccount_EmptyCookieError");
            return;
        }

        await VerifyAndCompleteAsync(cookie, isFromWebView: false);
    }

    private async Task VerifyAndCompleteAsync(string cookie, bool isFromWebView)
    {
        cookie = RobloxAuthService.NormalizeCookie(cookie);
        VerifyAddButton.IsEnabled = false;
        VerifyProgressRing.Visibility = Visibility.Visible;
        StatusText.Text = LocalizationService.Instance.GetString("Str_AddAccount_VerifyingStatus");

        try
        {
            using var auth = new RobloxAuthService();
            var user = await auth.GetAuthenticatedUserAsync(cookie);
            if (_dialogClosed) return;

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
            if (_dialogClosed) return;

            VerifyProgressRing.Visibility = Visibility.Collapsed;
            StatusText.Text = LocalizationService.Instance.Format("Str_AddAccount_FailedFormat", ex.Message);
            VerifyAddButton.IsEnabled = true;

            if (isFromWebView)
            {
                _lastRejectedCookie = cookie;
                _isVerifyingWebCookie = false;
                _cookiePollTimer?.Start();
            }
        }
    }

    private void SwitchToManualTab_Click(object sender, RoutedEventArgs e)
    {
        LoginTabControl.SelectedItem = ManualCookieTab;
    }

    private void DownloadWebView2_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(WebView2DownloadUrl) { UseShellExecute = true });
        }
        catch { }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _dialogClosed = true;
        _cookiePollTimer?.Stop();
        _cookiePollTimer = null;

        try
        {
            if (_webViewInitialized && LoginWebView.CoreWebView2 != null)
            {
                LoginWebView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            }
            LoginWebView.Dispose();
        }
        catch { }

        string? folderToDelete = _webViewUserDataFolder;
        if (!string.IsNullOrWhiteSpace(folderToDelete))
        {
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 6; i++)
                {
                    try
                    {
                        if (Directory.Exists(folderToDelete))
                        {
                            Directory.Delete(folderToDelete, recursive: true);
                        }
                        break;
                    }
                    catch
                    {
                        await Task.Delay(350);
                    }
                }
            });
        }
    }
}
