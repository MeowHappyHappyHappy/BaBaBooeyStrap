using System.Text;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Bootstrapper.Mods.Presets;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier3_CrossFeatureCombinations;

public sealed class FastFlags_With_ModsOverlayTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public FastFlags_With_ModsOverlayTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public void T3_FastFlags_ModsOverlay_ClientAppSettingsGeneratedInOverlaidDirectory()
    {
        string versionDir = _env.CreateDummyRobloxInstallation("version-t3-ffmods");
        string modClientSettings = Path.Combine(_env.ModificationsPath, "ClientSettings");
        Directory.CreateDirectory(modClientSettings);

        string modFfPath = Path.Combine(modClientSettings, "ClientAppSettings.json");
        var ffm = new FastFlagManager(modFfPath);
        ffm.SetFlag("FIntDebugForceMSAASamples", 4);
        ffm.SetFlag("FFlagDebugGraphicsPreferD3D11", "True");
        ffm.Save();

        string modTextureDir = Path.Combine(_env.ModificationsPath, "content", "textures");
        Directory.CreateDirectory(modTextureDir);
        File.WriteAllText(Path.Combine(modTextureDir, "custom_logo.png"), "mock_png_data");

        var manifest = ModOverlayEngine.ApplyModifications(_env.ModificationsPath, versionDir);

        string targetFfPath = Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json");
        File.Exists(targetFfPath).Should().BeTrue();

        string jsonContent = File.ReadAllText(targetFfPath);
        jsonContent.Should().Contain("\"FIntDebugForceMSAASamples\": \"4\"");
        jsonContent.Should().Contain("\"FFlagDebugGraphicsPreferD3D11\": \"True\"");

        manifest.Should().Contain("ClientSettings/ClientAppSettings.json");
        File.Exists(Path.Combine(versionDir, "content", "textures", "custom_logo.png")).Should().BeTrue();
    }

    [Fact]
    public void T3_FastFlags_ModsOverlay_CustomFontAndCursor_SynchronizedWithGraphicsFlags()
    {
        string versionDir = _env.CreateDummyRobloxInstallation("version-t3-fontcursor");
        byte[] fontBytes = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44 };

        string modFontsDir = Path.Combine(_env.ModificationsPath, "content", "fonts");
        Directory.CreateDirectory(modFontsDir);
        File.WriteAllBytes(Path.Combine(modFontsDir, "CustomFont.ttf"), fontBytes);

        CursorPresetHandler.ApplyCursorPreset(_env.ModificationsPath, CursorPreset.Classic2006);

        string versionFfPath = Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json");
        var ffm = new FastFlagManager(versionFfPath);
        ffm.ApplyPreset("Vulkan", true);
        ffm.SetFlag("FStringCustomFontPath", "CustomFont.ttf");
        ffm.Save();

        FontValidator.IsValidFont(fontBytes).Should().BeTrue();
        ModOverlayEngine.ApplyModifications(_env.ModificationsPath, versionDir);

        File.Exists(Path.Combine(versionDir, "content", "fonts", "CustomFont.ttf")).Should().BeTrue();
        File.Exists(Path.Combine(versionDir, "content", "textures", "Cursors", "KeyboardMouse", "ArrowCursor.png")).Should().BeTrue();

        ffm.Load();
        ffm.Flags.ContainsKey("FFlagDebugGraphicsPreferVulkan").Should().BeTrue();
        ffm.Flags["FStringCustomFontPath"].ToString().Should().Be("CustomFont.ttf");
    }

    [Fact]
    public void T3_FastFlags_ModsOverlay_CustomAudio_OggValidationAndDeploymentWithSoundFlags()
    {
        string versionDir = _env.CreateDummyRobloxInstallation("version-t3-audio");
        byte[] oggBytes = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00, 0xAA, 0xBB, 0xCC, 0xDD };

        DeathSoundPresetHandler.IsValidOgg(oggBytes).Should().BeTrue();
        DeathSoundPresetHandler.ApplyDeathSound(oggBytes, versionDir);

        string versionFfPath = Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json");
        var ffm = new FastFlagManager(versionFfPath);
        ffm.SetFlag("FFlagDebugCustomAudioDeathSound", "True");
        ffm.Save();

        string deathSoundPath = Path.Combine(versionDir, "content", "sounds", "ouch.ogg");
        File.Exists(deathSoundPath).Should().BeTrue();

        byte[] readBack = File.ReadAllBytes(deathSoundPath);
        readBack.Take(4).Should().Equal(0x4F, 0x67, 0x67, 0x53);

        ffm.Load();
        ffm.Flags["FFlagDebugCustomAudioDeathSound"].ToString().Should().Be("True");
    }

    [Fact]
    public void T3_FastFlags_ModsOverlay_EditorExport_FeedsDirectlyIntoModificationsFolder()
    {
        string versionDir = _env.CreateDummyRobloxInstallation("version-t3-editor");
        var editorVm = new FastFlagEditorViewModel();

        editorVm.AddFlag("DFIntTaskSchedulerTargetFps", "165");
        editorVm.AddFlag("FFlagEnableNewPhysicsSolver", "True");

        string exportedJson = editorVm.ExportJson();
        string targetJson = Path.Combine(_env.ModificationsPath, "ClientSettings", "ClientAppSettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(targetJson)!);
        File.WriteAllText(targetJson, exportedJson, Encoding.UTF8);

        ModOverlayEngine.ApplyModifications(_env.ModificationsPath, versionDir);

        var reloadedFfm = new FastFlagManager(Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json"));
        reloadedFfm.Load();

        reloadedFfm.Flags["DFIntTaskSchedulerTargetFps"].ToString().Should().Be("165");
        reloadedFfm.Flags["FFlagEnableNewPhysicsSolver"].ToString().Should().Be("True");
    }

    [Fact]
    public void T3_FastFlags_ModsOverlay_CleanModifications_PreservesOriginalClientAppSettings()
    {
        if (Directory.Exists(_env.ModificationsPath))
        {
            Directory.Delete(_env.ModificationsPath, recursive: true);
        }
        Directory.CreateDirectory(_env.ModificationsPath);

        string versionDir = _env.CreateDummyRobloxInstallation("version-t3-cleanmods");
        string originalFfPath = Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(originalFfPath)!);
        File.WriteAllText(originalFfPath, "{\n  \"Baseline\": \"1\"\n}");

        string modTextureDir = Path.Combine(_env.ModificationsPath, "content", "textures");
        Directory.CreateDirectory(modTextureDir);
        string modFile = Path.Combine(modTextureDir, "temp_overlay.png");
        File.WriteAllText(modFile, "overlay_data");

        var manifest = ModOverlayEngine.ApplyModifications(_env.ModificationsPath, versionDir);

        string overlaidFile = Path.Combine(versionDir, "content", "textures", "temp_overlay.png");
        File.Exists(overlaidFile).Should().BeTrue();

        ModOverlayEngine.CleanModifications(versionDir, manifest);

        File.Exists(overlaidFile).Should().BeFalse();
        File.Exists(originalFfPath).Should().BeTrue();

        string preservedJson = File.ReadAllText(originalFfPath);
        preservedJson.Should().Contain("\"Baseline\": \"1\"");
    }
}
