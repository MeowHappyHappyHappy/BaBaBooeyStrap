using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.App.ViewModels.Sections;
using BaBaBooeyStrap.App.Views.Pages;
using BaBaBooeyStrap.Bootstrapper.Mods.Presets;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace BaBaBooeyStrap.Tests.App;

public sealed class MainWindowSubViewModelsTests : IDisposable
{
    private readonly string _tempDir;

    public MainWindowSubViewModelsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BBBS_SubVmTests_{Guid.NewGuid():N}");
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

    [Fact]
    public void MainWindowViewModel_ComposesFiveSubViewModels_AndSaveSettingsPersistsAllSections()
    {
        var paths = new PathProvider(_tempDir);
        var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
        var stateMgr = new JsonManager<State>(paths.StateFile);

        var vm = new MainWindowViewModel(
            settingsMgr.Prop,
            paths: paths,
            stateManager: stateMgr);

        vm.LaunchSettings.Should().NotBeNull();
        vm.FastFlags.Should().NotBeNull();
        vm.Mods.Should().NotBeNull();
        vm.AccountManagement.Should().NotBeNull();
        vm.WindowIntegrations.Should().NotBeNull();

        vm.FastFlags.UseFastFlagManager = true;
        vm.FastFlags.SelectedRenderingMode = RenderingMode.Vulkan;
        vm.FastFlags.SelectedMSAAMode = MSAAMode.x4;
        vm.FastFlags.FrmQualityOverride = 9;
        vm.FastFlags.MeshLodLevel = 2;

        vm.AccountManagement.TargetName = "SavedPlace";
        vm.AccountManagement.LaunchTargetInput = "1818";
        vm.AccountManagement.LaunchJobId = "job-123";

        vm.LaunchSettings.Channel = "zflag";
        vm.WindowIntegrations.BootstrapperTitle = "RefactoredTitle";

        vm.SaveSettings(settingsMgr);

        vm.StatusMessage.Should().NotBeNullOrWhiteSpace();
        File.Exists(paths.GetClientSettingsPath()).Should().BeTrue();

        var reloadedSettings = new JsonManager<Settings>(paths.SettingsFile);
        reloadedSettings.Load();
        reloadedSettings.Prop.Channel.Should().Be("zflag");
        reloadedSettings.Prop.BootstrapperTitle.Should().Be("RefactoredTitle");

        var reloadedState = new JsonManager<State>(paths.StateFile);
        reloadedState.Load();
        reloadedState.Prop.LastTargetName.Should().Be("SavedPlace");
        reloadedState.Prop.LastTargetInput.Should().Be("1818");
        reloadedState.Prop.LastJobId.Should().Be("job-123");
    }

    [Fact]
    public void SubViewModels_ModsAndCleanerAndShortcuts_UpdateSharedStatusAndSettings()
    {
        var paths = new PathProvider(_tempDir);
        paths.EnsureDirectoriesExist();
        var settings = new Settings
        {
            CleanerOptions = CleanerOptions.OneDay,
            CleanerDirectories = ["BaBaBooeyStrapLogs"]
        };

        var vm = new MainWindowViewModel(settings, paths: paths);

        // Mods sub-VM
        vm.Mods.SelectedCursorPreset = CursorPreset.Classic2006;
        vm.Mods.ActiveModFileCount.Should().BeGreaterThan(0);
        vm.Mods.OldAvatarBackgroundEnabled = true;
        vm.StatusMessage.Should().NotBeNullOrWhiteSpace();

        // Cleaner in WindowIntegrations sub-VM
        string staleLog = Path.Combine(paths.Logs, "stale.log");
        File.WriteAllText(staleLog, "old");
        File.SetLastWriteTimeUtc(staleLog, DateTime.UtcNow.AddDays(-10));

        vm.WindowIntegrations.RunCleanerNow();
        File.Exists(staleLog).Should().BeFalse();
        vm.WindowIntegrations.CleanerStatusText.Should().NotBeNullOrWhiteSpace();

        // Cleaner checkbox helper
        vm.WindowIntegrations.CleanRobloxLogs = true;
        settings.CleanerDirectories.Should().Contain("RobloxLogs");
        vm.WindowIntegrations.CleanRobloxLogs = false;
        settings.CleanerDirectories.Should().NotContain("RobloxLogs");
    }

    [Fact]
    public void MainWindowViewModel_DetachLocalization_StopsUpdatingAfterDetach()
    {
        try
        {
            LocalizationService.Instance.ApplyLanguage(AppLanguage.English);
            var paths = new PathProvider(_tempDir);
            var vm = new MainWindowViewModel(new Settings(), paths: paths);
            vm.StatusMessage.Should().Be("Ready");

            vm.DetachLocalization();
            LocalizationService.Instance.ApplyLanguage(AppLanguage.TraditionalChinese);

            vm.StatusMessage.Should().Be("Ready");
        }
        finally
        {
            LocalizationService.Instance.ApplyLanguage(AppLanguage.English);
        }
    }

    [Fact]
    public void AllNinePages_WithComposedMainWindowViewModel_MeasureAndArrangeCleanlyOnStaThread()
    {
        Exception? threadEx = null;
        var bindingWarnings = new List<string>();
        int renderedPages = 0;

        var thread = new Thread(() =>
        {
            Window? host = null;
            var traceListener = new BindingWarningListener(bindingWarnings);
            PresentationTraceSources.DataBindingSource.Listeners.Add(traceListener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error | SourceLevels.Warning;

            try
            {
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

                var paths = new PathProvider(_tempDir);
                paths.EnsureDirectoriesExist();
                var settingsMgr = new JsonManager<Settings>(paths.SettingsFile);
                var stateMgr = new JsonManager<State>(paths.StateFile);
                var robloxStateMgr = new JsonManager<RobloxState>(paths.RobloxStateFile);
                var vm = new MainWindowViewModel(
                    settingsMgr.Prop,
                    paths: paths,
                    stateManager: stateMgr,
                    robloxStateManager: robloxStateMgr);

                host = new Window { Width = 1120, Height = 740 };
                host.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
                host.Resources.MergedDictionaries.Add(new ControlsDictionary());
                host.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/BaBaBooeyStrap;component/Resources/Styles/AppResources.xaml", UriKind.Absolute)
                });

                Page[] pages =
                {
                    new AccountsPage(),
                    new FastFlagsPage(),
                    new ModsPage(),
                    new AppearancePage(),
                    new BehaviourPage(),
                    new ChannelPage(),
                    new GlobalSettingsPage(),
                    new ShortcutsPage(),
                    new AboutPage()
                };

                foreach (var page in pages)
                {
                    page.DataContext = vm;
                    host.Content = page;
                    page.Measure(new Size(1120, 740));
                    page.Arrange(new Rect(0, 0, 1120, 740));
                    page.UpdateLayout();
                    renderedPages++;
                }

                vm.DetachLocalization();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(traceListener);
                if (host != null) host.Content = null;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(25));

        threadEx.Should().BeNull();
        renderedPages.Should().Be(9);
        bindingWarnings.Should().BeEmpty("all XAML bindings across all 9 pages must resolve cleanly against MainWindowViewModel sub-ViewModels");
    }

    private sealed class BindingWarningListener : TraceListener
    {
        private readonly List<string> _messages;
        public BindingWarningListener(List<string> messages) => _messages = messages;
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _messages.Add(message);
            }
        }
    }
}
