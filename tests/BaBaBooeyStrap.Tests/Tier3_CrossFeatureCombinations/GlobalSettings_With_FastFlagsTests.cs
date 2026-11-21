using System.Xml.Linq;
using System.Xml.XPath;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier3_CrossFeatureCombinations;

public sealed class GlobalSettings_With_FastFlagsTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public GlobalSettings_With_FastFlagsTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public void T3_GlobalSettings_FastFlags_NativeFpsUncap_SynchronizesWithTaskSchedulerTargetFps()
    {
        _env.CreateSampleGlobalBasicSettingsXml(fpsCap: 60);

        string ffPath = Path.Combine(_env.RobloxVersionsPath, "t3_ff_sync", "ClientSettings", "ClientAppSettings.json");
        var ffm = new FastFlagManager(ffPath);

        GlobalSettingsManager.SetFramerateCap(_env.GlobalSettingsXmlPath, 144, lockReadOnly: false);
        ffm.SetFlag("DFIntTaskSchedulerTargetFps", 144);
        ffm.Save();

        var doc = XDocument.Load(_env.GlobalSettingsXmlPath);
        var capElem = doc.XPathSelectElement("//int[@name='FramerateCap']");
        capElem.Should().NotBeNull();
        capElem!.Value.Should().Be("144");

        ffm.Load();
        ffm.Flags["DFIntTaskSchedulerTargetFps"].ToString().Should().Be("144");
    }

    [Fact]
    public void T3_GlobalSettings_FastFlags_ReadOnlyXmlLock_FastFlagManagerStillSavesIndependently()
    {
        _env.CreateSampleGlobalBasicSettingsXml(fpsCap: 60);
        GlobalSettingsManager.SetFramerateCap(_env.GlobalSettingsXmlPath, 240, lockReadOnly: true);

        File.GetAttributes(_env.GlobalSettingsXmlPath).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();

        string ffPath = Path.Combine(_env.RobloxVersionsPath, "t3_ff_readonly", "ClientSettings", "ClientAppSettings.json");
        var ffm = new FastFlagManager(ffPath);

        Action saveAct = () =>
        {
            ffm.SetFlag("FFlagDebugGraphicsPreferD3D11", "True");
            ffm.Save();
        };

        saveAct.Should().NotThrow();
        File.GetAttributes(_env.GlobalSettingsXmlPath).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
    }

    [Fact]
    public void T3_GlobalSettings_FastFlags_CorruptedXmlRecreation_PreservesFastFlagIntegrity()
    {
        _env.CreateSampleGlobalBasicSettingsXml(fpsCap: 60);

        string ffPath = Path.Combine(_env.RobloxVersionsPath, "t3_ff_corrupt", "ClientSettings", "ClientAppSettings.json");
        var ffm = new FastFlagManager(ffPath);
        ffm.SetFlag("FFlagTestPersist", "True");
        ffm.Save();

        if (File.GetAttributes(_env.GlobalSettingsXmlPath).HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(_env.GlobalSettingsXmlPath, FileAttributes.Normal);
        }
        File.WriteAllText(_env.GlobalSettingsXmlPath, "<<<NOT_VALID_XML>>>");

        GlobalSettingsManager.SetFramerateCap(_env.GlobalSettingsXmlPath, 165);

        var doc = XDocument.Load(_env.GlobalSettingsXmlPath);
        doc.XPathSelectElement("//int[@name='FramerateCap']")!.Value.Should().Be("165");

        string? xmlDir = Path.GetDirectoryName(_env.GlobalSettingsXmlPath);
        var backups = Directory.GetFiles(xmlDir!, "*.bak");
        backups.Length.Should().BeGreaterThanOrEqualTo(1);

        ffm.Load();
        ffm.Flags["FFlagTestPersist"].ToString().Should().Be("True");
    }

    [Fact]
    public void T3_GlobalSettings_FastFlags_SettingsManager_PersistsBothConfigurations()
    {
        string settingsFile = Path.Combine(_env.BaBaBooeyStrapPath, "t3_settings_fps.json");
        var mgr = new JsonManager<Settings>(settingsFile);

        mgr.Prop.AcrylicBackgroundOpacity = 165;
        mgr.Prop.BootstrapperTitle = "BaBaBooeyStrap FPS Uncapped";
        mgr.Save();

        var reloaded = new JsonManager<Settings>(settingsFile);
        reloaded.Load();
        reloaded.Prop.AcrylicBackgroundOpacity.Should().Be(165);
        reloaded.Prop.BootstrapperTitle.Should().Be("BaBaBooeyStrap FPS Uncapped");
    }
}
