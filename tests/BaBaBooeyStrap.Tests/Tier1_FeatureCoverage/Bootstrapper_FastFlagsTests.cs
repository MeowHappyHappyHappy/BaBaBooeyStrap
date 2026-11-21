using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Bootstrapper_FastFlagsTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public Bootstrapper_FastFlagsTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public void F12_FastFlagManager_SetAndSave_GeneratesValidClientAppSettingsJson()
    {
        // Arrange
        string jsonPath = Path.Combine(_env.RootPath, "ClientAppSettings_SaveTest.json");
        var manager = new FastFlagManager(jsonPath);

        // Act
        manager.SetFlag("FFlagDebugGraphicsPreferD3D11", true);
        manager.SetFlag("FIntDebugForceMSAASamples", 4);
        manager.Save();

        // Assert
        File.Exists(jsonPath).Should().BeTrue();
        string content = File.ReadAllText(jsonPath);
        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("FFlagDebugGraphicsPreferD3D11").GetString().Should().Be("True");
        doc.RootElement.GetProperty("FIntDebugForceMSAASamples").GetString().Should().Be("4");
    }

    [Fact]
    public void F12_FastFlagManager_MsaaPreset_SetsForceMsaaFlagCorrectly()
    {
        // Arrange
        string jsonPath = Path.Combine(_env.RootPath, "ClientAppSettings_Msaa.json");
        var manager = new FastFlagManager(jsonPath);

        // Act
        manager.ApplyPreset("Rendering.MSAA", 8);

        // Assert
        manager.Flags.Should().ContainKey("FIntDebugForceMSAASamples");
        manager.Flags["FIntDebugForceMSAASamples"].Should().Be(8);
    }

    [Fact]
    public void F12_FastFlagManager_GraphicsBackendPreset_SetsVulkanOrD3D11Flag()
    {
        // Arrange
        string jsonPath = Path.Combine(_env.RootPath, "ClientAppSettings_Backend.json");
        var manager = new FastFlagManager(jsonPath);

        // Act: Apply Vulkan preset
        manager.ApplyPreset("Rendering.Vulkan", "True");

        // Assert: Vulkan is enabled and D3D11 is removed
        manager.Flags.Should().ContainKey("FFlagDebugGraphicsPreferVulkan");
        manager.Flags.Should().NotContainKey("FFlagDebugGraphicsPreferD3D11");

        // Act: Apply D3D11 preset
        manager.ApplyPreset("Rendering.D3D11", "True");

        // Assert: D3D11 is enabled and Vulkan is removed
        manager.Flags.Should().ContainKey("FFlagDebugGraphicsPreferD3D11");
        manager.Flags.Should().NotContainKey("FFlagDebugGraphicsPreferVulkan");
    }

    [Fact]
    public void F12_FastFlagManager_BulkImportExport_RoundTripsJsonProfile()
    {
        // Arrange
        string jsonPath = Path.Combine(_env.RootPath, "ClientAppSettings_Profile.json");
        var manager = new FastFlagManager(jsonPath);

        for (int i = 0; i < 10; i++)
        {
            manager.SetFlag($"FFlagCustom_{i}", i % 2 == 0);
        }

        // Act: Export to JSON string and reload in fresh manager
        string exportedJson = manager.ExportJson();

        var freshManager = new FastFlagManager(jsonPath);
        freshManager.ImportJson(exportedJson);

        // Assert
        freshManager.Flags.Count.Should().Be(10);
        for (int i = 0; i < 10; i++)
        {
            freshManager.Flags.Should().ContainKey($"FFlagCustom_{i}");
            freshManager.Flags[$"FFlagCustom_{i}"].Should().Be(i % 2 == 0);
        }
    }

    [Fact]
    public void F12_FastFlagManager_DeleteFlag_RemovesSpecifiedFlagAndPersists()
    {
        // Arrange
        string jsonPath = Path.Combine(_env.RootPath, "ClientAppSettings_Delete.json");
        var manager = new FastFlagManager(jsonPath);
        manager.SetFlag("FIntOldFlag", 123);
        manager.SetFlag("FIntKeepFlag", 456);
        manager.Save();

        // Act
        bool removed = manager.RemoveFlag("FIntOldFlag");
        manager.Save();

        // Assert
        removed.Should().BeTrue();
        var reloaded = new FastFlagManager(jsonPath);
        reloaded.Load();
        reloaded.Flags.Should().NotContainKey("FIntOldFlag");
        reloaded.Flags.Should().ContainKey("FIntKeepFlag");
    }
}
