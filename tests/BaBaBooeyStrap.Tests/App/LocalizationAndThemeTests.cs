using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.Views.Pages;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;
using FluentAssertions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Xunit;

namespace BaBaBooeyStrap.Tests.App;

[CollectionDefinition("LocalizationSingleton", DisableParallelization = true)]
public sealed class LocalizationSingletonCollection
{
}

[Collection("LocalizationSingleton")]
public sealed partial class LocalizationAndThemeTests : IDisposable
{
    private readonly string _tempDir;

    public LocalizationAndThemeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BBBS_L10n_Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [GeneratedRegex(@"\{(\d+)(?:[^}]*)\}")]
    private static partial Regex FormatPlaceholderRegex();

    [Fact]
    public void Catalogs_EnUsAndZhTw_HaveIdenticalKeysAndFormatPlaceholders()
    {
        var service = new LocalizationService();
        var en = service.GetCatalog(LocalizationService.EnglishTag);
        var zh = service.GetCatalog(LocalizationService.TraditionalChineseTag);

        en.Should().NotBeEmpty();
        zh.Should().NotBeEmpty();
        zh.Keys.Should().BeEquivalentTo(en.Keys);

        foreach (var key in en.Keys)
        {
            en[key].Should().NotBeNullOrWhiteSpace($"en-US key '{key}' must have a non-empty value");
            zh[key].Should().NotBeNullOrWhiteSpace($"zh-TW key '{key}' must have a non-empty value");

            var enPlaceholders = FormatPlaceholderRegex().Matches(en[key])
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var zhPlaceholders = FormatPlaceholderRegex().Matches(zh[key])
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            zhPlaceholders.Should().Equal(enPlaceholders, $"format placeholders for key '{key}' must match between en-US and zh-TW");
        }
    }

    [Theory]
    [InlineData(AppLanguage.Auto, "zh-TW", LocalizationService.TraditionalChineseTag)]
    [InlineData(AppLanguage.Auto, "zh-HK", LocalizationService.TraditionalChineseTag)]
    [InlineData(AppLanguage.Auto, "zh-MO", LocalizationService.TraditionalChineseTag)]
    [InlineData(AppLanguage.Auto, "zh-Hant", LocalizationService.TraditionalChineseTag)]
    [InlineData(AppLanguage.Auto, "zh-CN", LocalizationService.EnglishTag)]
    [InlineData(AppLanguage.Auto, "en-US", LocalizationService.EnglishTag)]
    [InlineData(AppLanguage.Auto, "ja-JP", LocalizationService.EnglishTag)]
    [InlineData(AppLanguage.TraditionalChinese, "en-US", LocalizationService.TraditionalChineseTag)]
    [InlineData(AppLanguage.English, "zh-TW", LocalizationService.EnglishTag)]
    public void ResolveEffectiveLanguageTag_HandlesAutoAndExplicitLanguages(
        AppLanguage language,
        string cultureName,
        string expectedTag)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        string actual = LocalizationService.ResolveEffectiveLanguageTag(language, culture);
        actual.Should().Be(expectedTag);
    }

    [Fact]
    public void ApplyLanguage_LiveSwitchesStringsAndEnumDisplay_AndFallsBackToEnglish()
    {
        var service = new LocalizationService();
        var observedTags = new List<string>();
        service.LanguageChanged += (_, tag) => observedTags.Add(tag);

        service.CurrentLanguageTag.Should().Be(LocalizationService.EnglishTag);
        service.GetString("Str_MainWindow_Save").Should().Be("Save");
        service.GetEnumDisplay(Theme.Dark).Should().Be("Dark");

        service.ApplyLanguage(AppLanguage.TraditionalChinese);

        observedTags.Should().ContainSingle().Which.Should().Be(LocalizationService.TraditionalChineseTag);
        service.CurrentLanguageTag.Should().Be(LocalizationService.TraditionalChineseTag);
        service.GetString("Str_MainWindow_Save").Should().Be("儲存");
        service.GetEnumDisplay(Theme.Dark).Should().Be("深色");
        service.GetEnumDisplay(CleanerOptions.TwoWeeks).Should().Be("2 週");
        service.GetString("NonExistent_Localization_Key").Should().Be("NonExistent_Localization_Key");

        service.ApplyLanguage(AppLanguage.English);
        service.CurrentLanguageTag.Should().Be(LocalizationService.EnglishTag);
        service.GetString("Str_MainWindow_Save").Should().Be("Save");
    }

    [Fact]
    public void Settings_LanguagePersistence_IsIndependentOfRobloxLocale()
    {
        string settingsPath = Path.Combine(_tempDir, "Settings.json");
        var manager = new JsonManager<Settings>(settingsPath);
        manager.Prop.Language = AppLanguage.TraditionalChinese;
        manager.Prop.Locale = "ja_jp";
        manager.Save();

        var reloaded = new JsonManager<Settings>(settingsPath);
        reloaded.Load();
        reloaded.Prop.Language.Should().Be(AppLanguage.TraditionalChinese);
        reloaded.Prop.Locale.Should().Be("ja_jp");
    }

    [Fact]
    public void MainWindowViewModel_LanguageSwitch_UpdatesDynamicTextsAndPreservesNavigationTabsContract()
    {
        try
        {
            LocalizationService.Instance.ApplyLanguage(AppLanguage.English);
            var settings = new Settings();
            var vm = new MainWindowViewModel(settings);

            vm.NavigationTabs.Should().Equal(
                "Accounts", "FastFlags", "Mods", "Appearance", "Behaviour",
                "Channel", "Global Settings", "Shortcuts", "About");

            vm.WindowIntegrations.AvailableLanguages.Should().Contain(new[]
            {
                AppLanguage.Auto,
                AppLanguage.TraditionalChinese,
                AppLanguage.English
            });

            vm.WindowIntegrations.CurrentLanguage = AppLanguage.TraditionalChinese;

            settings.Language.Should().Be(AppLanguage.TraditionalChinese);
            LocalizationService.Instance.CurrentLanguageTag.Should().Be(LocalizationService.TraditionalChineseTag);
            vm.StatusMessage.Should().Be("就緒");
            vm.LaunchSettings.CdnTestResult.Should().Be("尚未測試");
            vm.AccountManagement.MultiInstanceStatusText.Should().StartWith("多開狀態：");
            vm.NavigationTabs.Should().Equal(
                "Accounts", "FastFlags", "Mods", "Appearance", "Behaviour",
                "Channel", "Global Settings", "Shortcuts", "About");
        }
        finally
        {
            LocalizationService.Instance.ApplyLanguage(AppLanguage.English);
        }
    }

    [Theory]
    [InlineData(Theme.Light, SystemTheme.Dark, ApplicationTheme.Light)]
    [InlineData(Theme.Dark, SystemTheme.Light, ApplicationTheme.Dark)]
    [InlineData(Theme.Default, SystemTheme.Light, ApplicationTheme.Light)]
    [InlineData(Theme.Default, SystemTheme.HCWhite, ApplicationTheme.Light)]
    [InlineData(Theme.Default, SystemTheme.Dark, ApplicationTheme.Dark)]
    [InlineData(Theme.Default, SystemTheme.HCBlack, ApplicationTheme.Dark)]
    public void ThemeService_ResolveApplicationTheme_RespectsLightDarkAndDefaultSystemTheme(
        Theme requestedTheme,
        SystemTheme simulatedSystemTheme,
        ApplicationTheme expectedAppTheme)
    {
        var resolved = ThemeService.ResolveApplicationTheme(requestedTheme, simulatedSystemTheme);
        resolved.Should().Be(expectedAppTheme);
    }

    [Fact]
    public void AllPages_OnStaThread_SetDynamicTextFillColorPrimaryForeground()
    {
        Exception? threadEx = null;
        var pageColors = new Dictionary<string, Color>();
        Color? lightThemeColor = null;

        var thread = new Thread(() =>
        {
            Window? host = null;
            try
            {
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
                LocalizationService.Instance.ApplyLanguage(AppLanguage.TraditionalChinese);

                host = new Window();
                var darkTheme = new ThemesDictionary { Theme = ApplicationTheme.Dark };
                host.Resources.MergedDictionaries.Add(darkTheme);
                host.Resources.MergedDictionaries.Add(new ControlsDictionary());
                host.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/BaBaBooeyStrap;component/Resources/Styles/AppResources.xaml", UriKind.Absolute)
                });

                Page[] pages =
                {
                    new AccountsPage(),
                    new AppearancePage(),
                    new BehaviourPage(),
                    new ChannelPage(),
                    new FastFlagsPage(),
                    new GlobalSettingsPage(),
                    new ModsPage(),
                    new ShortcutsPage(),
                    new AboutPage()
                };

                foreach (var page in pages)
                {
                    host.Content = page;

                    var brush = page.Foreground as SolidColorBrush;
                    brush.Should().NotBeNull($"{page.GetType().Name}.Foreground must resolve to a SolidColorBrush in Dark theme");
                    Color c = brush!.Color;
                    pageColors[page.GetType().Name] = c;

                    double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                    luminance.Should().BeGreaterThan(0.6, $"{page.GetType().Name} must have light-colored text in Dark mode (actual {c})");

                    var textBrush = System.Windows.Documents.TextElement.GetForeground(page) as SolidColorBrush;
                    textBrush.Should().NotBeNull($"{page.GetType().Name} TextElement.Foreground must resolve to a SolidColorBrush in Dark theme");
                    double textLuminance = (0.2126 * textBrush!.Color.R + 0.7152 * textBrush.Color.G + 0.0722 * textBrush.Color.B) / 255.0;
                    textLuminance.Should().BeGreaterThan(0.6, $"{page.GetType().Name} TextElement.Foreground must be light-colored in Dark mode (actual {textBrush.Color})");
                }

                // Swap to Light theme dynamically on the last attached page (AboutPage)
                int themeIndex = host.Resources.MergedDictionaries.IndexOf(darkTheme);
                host.Resources.MergedDictionaries[themeIndex] = new ThemesDictionary { Theme = ApplicationTheme.Light };

                var lastPage = pages[^1];
                var lightBrush = lastPage.Foreground as SolidColorBrush;
                lightBrush.Should().NotBeNull("AboutPage.Foreground must resolve to a SolidColorBrush in Light theme");
                lightThemeColor = lightBrush!.Color;
                double lightLuminance = (0.2126 * lightBrush.Color.R + 0.7152 * lightBrush.Color.G + 0.0722 * lightBrush.Color.B) / 255.0;
                lightLuminance.Should().BeLessThan(0.3, $"AboutPage must switch to dark text in Light mode (actual {lightBrush.Color})");
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
            finally
            {
                if (host != null)
                {
                    host.Content = null;
                }
                LocalizationService.Instance.ApplyLanguage(AppLanguage.English);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(20));

        threadEx.Should().BeNull();
        pageColors.Should().HaveCount(9);
        lightThemeColor.Should().NotBeNull();
    }
}
