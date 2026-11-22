using System.Xml.Linq;
using System.Xml.XPath;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Bootstrapper_GlobalSettingsTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public Bootstrapper_GlobalSettingsTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public void F15_GlobalSettings_UnblockFpsCap_SetsFramerateCapValue()
    {
        // Arrange
        string xmlPath = Path.Combine(_env.RootPath, "GlobalBasicSettings_Fps240.xml");
        GlobalSettingsManager.EnsureConfigured(xmlPath, defaultFps: 60);

        // Act
        GlobalSettingsManager.SetFramerateCap(xmlPath, 240);

        // Assert
        var doc = XDocument.Load(xmlPath);
        var elem = doc.XPathSelectElement("//int[@name='FramerateCap']");
        elem.Should().NotBeNull();
        elem!.Value.Should().Be("240");
    }

    [Fact]
    public void F15_GlobalSettings_ReadOnlyFile_UnlocksModifiesAndRestoresLock()
    {
        // Arrange
        string xmlPath = Path.Combine(_env.RootPath, "GlobalBasicSettings_ReadOnly.xml");
        GlobalSettingsManager.EnsureConfigured(xmlPath, defaultFps: 60);
        File.SetAttributes(xmlPath, FileAttributes.ReadOnly);

        // Act
        GlobalSettingsManager.SetFramerateCap(xmlPath, 144, lockReadOnly: true);

        // Assert
        var attr = File.GetAttributes(xmlPath);
        attr.HasFlag(FileAttributes.ReadOnly).Should().BeTrue();

        // Read XML and verify value updated
        var doc = XDocument.Load(xmlPath);
        var elem = doc.XPathSelectElement("//int[@name='FramerateCap']");
        elem.Should().NotBeNull();
        elem!.Value.Should().Be("144");
    }

    [Fact]
    public void F15_GlobalSettings_PreservesUnrelatedProperties_DoesNotAlterOtherNodes()
    {
        // Arrange
        string xmlPath = Path.Combine(_env.RootPath, "GlobalBasicSettings_Preserve.xml");
        GlobalSettingsManager.EnsureConfigured(xmlPath, defaultFps: 60);

        // Act: Update FramerateCap
        GlobalSettingsManager.SetFramerateCap(xmlPath, 165);

        // Assert: MouseSensitivity and SavedQualityLevel preserved
        var doc = XDocument.Load(xmlPath);
        var sens = doc.XPathSelectElement("//float[@name='MouseSensitivity']");
        var qual = doc.XPathSelectElement("//token[@name='SavedQualityLevel']");
        sens.Should().NotBeNull();
        sens!.Value.Should().Be("1.5");
        qual.Should().NotBeNull();
        qual!.Value.Should().Be("10");
    }

    [Fact]
    public void F15_GlobalSettings_MissingFile_GeneratesDefaultCompliantXml()
    {
        // Arrange
        string xmlPath = Path.Combine(_env.RootPath, "GlobalBasicSettings_Missing.xml");
        if (File.Exists(xmlPath)) File.Delete(xmlPath);

        // Act
        GlobalSettingsManager.EnsureConfigured(xmlPath, defaultFps: 120);

        // Assert
        File.Exists(xmlPath).Should().BeTrue();
        var doc = XDocument.Load(xmlPath);
        doc.Root?.Name.LocalName.Should().Be("roblox");
        var elem = doc.XPathSelectElement("//int[@name='FramerateCap']");
        elem.Should().NotBeNull();
        elem!.Value.Should().Be("120");
    }

    [Fact]
    public void F15_GlobalSettings_XPathQuery_AccuratelyLocatesFramerateCapNode()
    {
        // Arrange
        string xmlPath = Path.Combine(_env.RootPath, "GlobalBasicSettings_Query.xml");
        GlobalSettingsManager.EnsureConfigured(xmlPath, defaultFps: 360);

        // Act
        var doc = XDocument.Load(xmlPath);
        var elem = doc.XPathSelectElement("/roblox/Item[@class='GlobalBasicSettings']/Properties/int[@name='FramerateCap']");

        // Assert
        elem.Should().NotBeNull();
        int.Parse(elem!.Value).Should().Be(360);
    }
}
