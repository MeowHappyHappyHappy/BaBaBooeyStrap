using System.Reflection;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier3_CrossFeatureCombinations;

[Collection("LocalizationSingleton")]
public sealed class AppUi_With_SystemTrayAndThemesTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public AppUi_With_SystemTrayAndThemesTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public void T3_AppUi_SystemTray_MinimizeToTray_PreservesMainWindowStateAndKeepsProcessAlive()
    {
        var vm = new MainWindowViewModel(new Settings { Theme = Theme.Dark });
        using var tray = new NotifyIconWrapper();
        tray.Initialize();

        tray.MinimizeToTray();

        tray.IsWindowVisible.Should().BeFalse();
        tray.IsTrayActive.Should().BeTrue();
        vm.CurrentTheme.Should().Be(Theme.Dark);
    }

    [Fact]
    public void T3_AppUi_SystemTray_RestoreFromTray_RestoresWindowVisibilityAndRefreshesAccounts()
    {
        string dbPath = Path.Combine(_env.BaBaBooeyStrapPath, "t3_tray_accounts.dat");
        var store = new DpapiAccountStore(dbPath);
        store.Upsert(new RobloxAccount { UserId = 5566, Username = "TrayPlayer", Cookie = "test_cookie" });

        var vm = new MainWindowViewModel(new Settings(), store);
        using var tray = new NotifyIconWrapper();
        tray.Initialize();
        tray.MinimizeToTray();

        tray.RestoreWindow();
        vm.ReloadAccounts();

        tray.IsWindowVisible.Should().BeTrue();
        vm.Accounts.Should().ContainSingle(a => a.Username == "TrayPlayer");
    }

    [Fact]
    public void T3_AppUi_XmlThemes_CustomThemeLoaded_UpdatesMainWindowThemeColors()
    {
        string neonXml = """
            <Theme Name="NeonCyber">
              <Colors Background="#FF0F0F1A" Foreground="#FF00FFCC"/>
            </Theme>
            """;

        var theme = XmlThemeParser.Parse(neonXml);

        theme.Background.Should().Be("#FF0F0F1A");
        theme.Foreground.Should().Be("#FF00FFCC");
    }

    [Fact]
    public void T3_AppUi_StandaloneBuild_AssemblyMetadataAndPathsConsistency()
    {
        var paths = new PathProvider(_env.BaBaBooeyStrapPath);
        paths.EnsureDirectoriesExist();

        Path.GetFileName(paths.Application).Should().Be("BaBaBooeyStrap.exe");
        Directory.Exists(paths.Logs).Should().BeTrue();
        Directory.Exists(paths.CustomThemes).Should().BeTrue();

        var assemblies = new[]
        {
            typeof(PathProvider).Assembly,
            typeof(WindowsMultiInstanceController).Assembly,
            typeof(PackageExtractor).Assembly
        };

        foreach (var asm in assemblies)
        {
            asm.GetName().Version.Should().NotBeNull();
            asm.ManifestModule.GetPEKind(out var peKinds, out var machine);
            machine.Should().Be(ImageFileMachine.AMD64);
            peKinds.HasFlag(PortableExecutableKinds.ILOnly).Should().BeTrue();
        }
    }
}
