using System.Xml.Linq;
using System.Xml.XPath;
using BaBaBooeyStrap.Bootstrapper.Settings;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Bootstrapper;

public sealed class GlobalSettingsManagerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _xmlPath;

    public GlobalSettingsManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "GlobalSettingsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _xmlPath = Path.Combine(_tempDirectory, "GlobalBasicSettings_13.xml");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_xmlPath))
            {
                var attr = File.GetAttributes(_xmlPath);
                if (attr.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(_xmlPath, attr & ~FileAttributes.ReadOnly);
                }
            }
            if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true);
        }
        catch { }
    }

    [Fact]
    public void EnsureTemplateExists_MissingFile_GeneratesStandardCompliantXml()
    {
        // Arrange
        var manager = new GlobalSettingsManager(_xmlPath);

        // Act
        manager.EnsureTemplateExists(defaultFps: 144);

        // Assert
        File.Exists(_xmlPath).Should().BeTrue();
        var doc = XDocument.Load(_xmlPath);
        doc.Root?.Name.LocalName.Should().Be("roblox");

        var capElem = doc.XPathSelectElement("//int[@name='FramerateCap']");
        capElem.Should().NotBeNull();
        capElem!.Value.Should().Be("144");
    }

    [Fact]
    public void FramerateCap_GetAndSet_UpdatesXmlAndClampsExtremeValues()
    {
        // Arrange
        var manager = new GlobalSettingsManager(_xmlPath);
        manager.EnsureTemplateExists(defaultFps: 60);

        // Act & Assert: Standard update
        manager.FramerateCap = 240;
        manager.Save();
        manager.FramerateCap.Should().Be(240);

        // Act & Assert: Clamping negative values to 0
        manager.FramerateCap = -60;
        manager.Save();
        manager.FramerateCap.Should().Be(0);

        // Act & Assert: Clamping huge values to 1000
        manager.FramerateCap = 999999;
        manager.Save();
        manager.FramerateCap.Should().Be(1000);
    }

    [Fact]
    public void MouseSensitivity_SetsScalarAndBothVector2Properties()
    {
        // Arrange
        var manager = new GlobalSettingsManager(_xmlPath);
        manager.EnsureTemplateExists();

        // Act
        manager.MouseSensitivity = 0.36f;
        manager.Save();

        // Assert
        var doc = XDocument.Load(_xmlPath);
        var scalar = doc.XPathSelectElement("//float[@name='MouseSensitivity']");
        scalar.Should().NotBeNull();
        float.Parse(scalar!.Value).Should().BeApproximately(0.36f, 0.001f);

        var fpX = doc.XPathSelectElement("//Vector2[@name='MouseSensitivityFirstPerson']/X");
        var fpY = doc.XPathSelectElement("//Vector2[@name='MouseSensitivityFirstPerson']/Y");
        var tpX = doc.XPathSelectElement("//Vector2[@name='MouseSensitivityThirdPerson']/X");
        var tpY = doc.XPathSelectElement("//Vector2[@name='MouseSensitivityThirdPerson']/Y");

        fpX.Should().NotBeNull();
        fpY.Should().NotBeNull();
        tpX.Should().NotBeNull();
        tpY.Should().NotBeNull();

        float.Parse(fpX!.Value).Should().BeApproximately(0.36f, 0.001f);
        float.Parse(tpX!.Value).Should().BeApproximately(0.36f, 0.001f);
    }

    [Fact]
    public void ReadOnlyLock_UnlocksModifiesAndRestoresReadOnlyAttribute()
    {
        // Arrange
        var manager = new GlobalSettingsManager(_xmlPath);
        manager.EnsureTemplateExists();
        manager.ReadOnlyLocked = true;

        File.GetAttributes(_xmlPath).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();

        // Act: Modify while read-only locked
        manager.FramerateCap = 360;
        manager.Save();

        // Assert: Saved correctly and read-only attribute was preserved
        File.GetAttributes(_xmlPath).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();

        var reloaded = new GlobalSettingsManager(_xmlPath);
        reloaded.Load();
        reloaded.FramerateCap.Should().Be(360);
    }

    [Fact]
    public void CorruptedXml_CreatesBackupAndReinitializes()
    {
        // Arrange
        File.WriteAllText(_xmlPath, "<roblox><Item><Unclosed>");
        var manager = new GlobalSettingsManager(_xmlPath);

        // Act: Loading or setting should create .corrupted backup and recover
        manager.FramerateCap = 165;
        manager.Save();

        // Assert
        File.Exists(_xmlPath).Should().BeTrue();
        var backups = Directory.GetFiles(_tempDirectory, "GlobalBasicSettings_13.xml.corrupted.*.bak");
        backups.Length.Should().Be(1);

        var doc = XDocument.Load(_xmlPath);
        doc.XPathSelectElement("//int[@name='FramerateCap']")!.Value.Should().Be("165");
    }

    [Fact]
    public void StaticSetFramerateCap_UpdatesFileDirectly()
    {
        // Arrange
        GlobalSettingsManager.EnsureConfigured(_xmlPath, defaultFps: 60);

        // Act
        GlobalSettingsManager.SetFramerateCap(_xmlPath, 165, lockReadOnly: true);

        // Assert
        File.GetAttributes(_xmlPath).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
        var doc = XDocument.Load(_xmlPath);
        doc.XPathSelectElement("//int[@name='FramerateCap']")!.Value.Should().Be("165");
    }

    [Fact]
    public void StaticSetFramerateCap_WhenFileAlreadyReadOnly_UnlocksModifiesAndRestoresReadOnly()
    {
        // Arrange: Prepare file and mark ReadOnly beforehand
        GlobalSettingsManager.EnsureConfigured(_xmlPath, defaultFps: 60);
        File.SetAttributes(_xmlPath, FileAttributes.ReadOnly);
        File.GetAttributes(_xmlPath).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();

        // Act: Invoke static SetFramerateCap on read-only file
        GlobalSettingsManager.SetFramerateCap(_xmlPath, 144, lockReadOnly: true);

        // Assert: ReadOnly preserved and FramerateCap updated to 144
        File.GetAttributes(_xmlPath).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
        var doc = XDocument.Load(_xmlPath);
        doc.XPathSelectElement("//int[@name='FramerateCap']")!.Value.Should().Be("144");
    }
}
