using System.IO;
using System.Text;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Bootstrapper.Mods.Presets;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier4_RealWorldScenarios;

public sealed class Scenario3_CustomModdedSessionWithFpsUncapTests
{
    [Fact]
    public void Scenario3_FullAssetModdingAndOverlay_MirrorsFontsAudioAndCursorsSafely()
    {
        // 1. Arrange Base Installation
        using var env = new MockRobloxEnvironment();
        string versionDir = env.CreateDummyRobloxInstallation("version-mod-test");
        env.CreateSampleGlobalBasicSettingsXml(fpsCap: 60);

        // 2. Validate & Write Mod Assets
        byte[] ttfBytes = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44 };
        FontValidator.IsValidFont(ttfBytes).Should().BeTrue();
        string fontDest = Path.Combine(env.ModificationsPath, "content", "fonts", "CustomFont.ttf");
        Directory.CreateDirectory(Path.GetDirectoryName(fontDest)!);
        File.WriteAllBytes(fontDest, ttfBytes);

        byte[] oggBytes = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00 };
        DeathSoundPresetHandler.IsValidOgg(oggBytes).Should().BeTrue();
        DeathSoundPresetHandler.ApplyDeathSound(oggBytes, env.ModificationsPath);

        CursorPresetHandler.ApplyCursorPreset(env.ModificationsPath, CursorPreset.Classic2006);

        // 3. Mirror Modifications Overlay into Version Folder (F13)
        var manifest = ModOverlayEngine.ApplyModifications(env.ModificationsPath, versionDir);
        manifest.Count.Should().BeGreaterThanOrEqualTo(3);

        File.Exists(Path.Combine(versionDir, "content", "fonts", "CustomFont.ttf")).Should().BeTrue();
        File.Exists(Path.Combine(versionDir, "content", "sounds", "ouch.ogg")).Should().BeTrue();
        File.Exists(Path.Combine(versionDir, "content", "textures", "Cursors", "KeyboardMouse", "ArrowCursor.png")).Should().BeTrue();

        // 4. Configure FastFlags (F12)
        string clientSettingsJson = Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json");
        var ffManager = new FastFlagManager(clientSettingsJson);
        ffManager.ApplyPreset("MSAA", 8);
        ffManager.ApplyPreset("Vulkan", true);
        ffManager.Save();

        // 5. Global Settings FPS Uncap & Read-Only Lock (F15)
        GlobalSettingsManager.SetFramerateCap(env.GlobalSettingsXmlPath, 240, lockReadOnly: true);
        var attr = File.GetAttributes(env.GlobalSettingsXmlPath);
        attr.HasFlag(FileAttributes.ReadOnly).Should().BeTrue();

        // Verify Read-Only prevents direct write
        var writeAction = () => File.WriteAllText(env.GlobalSettingsXmlPath, "corrupted");
        writeAction.Should().Throw<UnauthorizedAccessException>();

        // 6. Window Manipulation Hooks (F18)
        using var testWin = new NativeTestWindow("OriginalRobloxTitle", visible: true);
        WindowManipulator.ApplyTitle(testWin.Handle, "BaBaBooeyStrap [FPS: 240] [Mods: Active]").Should().BeTrue();
        WindowManipulator.SetBorderlessFullscreen(testWin.Handle, true, 0, 0, 1920, 1080).Should().BeTrue();

        // 7. Mod Cleanup Reversion (F13)
        ModOverlayEngine.CleanModifications(versionDir, manifest);
        File.Exists(Path.Combine(versionDir, "content", "fonts", "CustomFont.ttf")).Should().BeFalse();
        File.Exists(Path.Combine(versionDir, "RobloxPlayerBeta.exe")).Should().BeTrue(); // Base binary preserved!
    }

    [Fact]
    public void Scenario3_NativeFpsUncap_EditsXmlAndEnforcesReadOnlyProtectionAgainstReset()
    {
        using var env = new MockRobloxEnvironment();
        env.CreateSampleGlobalBasicSettingsXml(fpsCap: 60);

        // 1. Set Framerate cap to 240 and lock read-only
        GlobalSettingsManager.SetFramerateCap(env.GlobalSettingsXmlPath, 240, lockReadOnly: true);

        File.Exists(env.GlobalSettingsXmlPath).Should().BeTrue();
        var attr = File.GetAttributes(env.GlobalSettingsXmlPath);
        attr.HasFlag(FileAttributes.ReadOnly).Should().BeTrue();

        string content = File.ReadAllText(env.GlobalSettingsXmlPath);
        content.Should().Contain("240");

        // Direct overwrite fails while locked
        var writeAttempt = () => File.WriteAllText(env.GlobalSettingsXmlPath, "overwrite");
        writeAttempt.Should().Throw<UnauthorizedAccessException>();

        // 2. GlobalSettingsManager unlocks, modifies, and restores read-only protection seamlessly
        GlobalSettingsManager.SetFramerateCap(env.GlobalSettingsXmlPath, 165);

        var attrRestored = File.GetAttributes(env.GlobalSettingsXmlPath);
        attrRestored.HasFlag(FileAttributes.ReadOnly).Should().BeTrue();

        string updatedContent = File.ReadAllText(env.GlobalSettingsXmlPath);
        updatedContent.Should().Contain("165");

        // 3. Explicit removal of read-only allows external direct write
        File.SetAttributes(env.GlobalSettingsXmlPath, attrRestored & ~FileAttributes.ReadOnly);
        File.WriteAllText(env.GlobalSettingsXmlPath, "<roblox><Item class=\"GlobalBasicSettings\"><Properties><int name=\"FramerateCap\">120</int></Properties></Item></roblox>");
        File.ReadAllText(env.GlobalSettingsXmlPath).Should().Contain("120");
    }

    [Fact]
    public void Scenario3_WindowManipulation_AppliesCustomTitleAndBorderlessFullscreenState()
    {
        using var testWindow = new NativeTestWindow("BaBaBooeyStrap_BaseWin", visible: true);

        string customTitle = "BaBaBooeyStrap - Client Instance #1 [Target FPS: 240]";
        bool titleResult = WindowManipulator.ApplyTitle(testWindow.Handle, customTitle);
        titleResult.Should().BeTrue();

        var titleBuffer = new StringBuilder(256);
        User32.GetWindowTextW(testWindow.Handle, titleBuffer, 256);
        titleBuffer.ToString().Should().Be(customTitle);

        // Apply borderless fullscreen state
        bool borderlessApplied = WindowManipulator.SetBorderlessFullscreen(testWindow.Handle, true, 0, 0, 1920, 1080);
        borderlessApplied.Should().BeTrue();

        // Restore windowed state
        bool windowedRestored = WindowManipulator.SetBorderlessFullscreen(testWindow.Handle, false, 50, 50, 1024, 768);
        windowedRestored.Should().BeTrue();
    }

    [Fact]
    public void Scenario3_ModCleanReversion_RemovesOverlayFilesWithoutDamagingBaseBinaries()
    {
        using var env = new MockRobloxEnvironment();
        string versionDir = env.CreateDummyRobloxInstallation("version-clean-reversion");

        // Base binary and texture
        string baseExe = Path.Combine(versionDir, "RobloxPlayerBeta.exe");
        string baseTexture = Path.Combine(versionDir, "content", "textures", "default_icon.png");
        Directory.CreateDirectory(Path.GetDirectoryName(baseTexture)!);
        File.WriteAllText(baseTexture, "base_texture_data");

        // Mod overlay files in Modifications folder
        string modTexture = Path.Combine(env.ModificationsPath, "content", "textures", "custom_cursor.png");
        string modFont = Path.Combine(env.ModificationsPath, "content", "fonts", "custom_hud.ttf");
        Directory.CreateDirectory(Path.GetDirectoryName(modTexture)!);
        Directory.CreateDirectory(Path.GetDirectoryName(modFont)!);
        File.WriteAllText(modTexture, "mod_cursor_data");
        File.WriteAllText(modFont, "mod_font_data");

        // Apply mods
        var manifest = ModOverlayEngine.ApplyModifications(env.ModificationsPath, versionDir);
        manifest.Should().HaveCount(2);

        string deployedModTexture = Path.Combine(versionDir, "content", "textures", "custom_cursor.png");
        string deployedModFont = Path.Combine(versionDir, "content", "fonts", "custom_hud.ttf");
        File.Exists(deployedModTexture).Should().BeTrue();
        File.Exists(deployedModFont).Should().BeTrue();

        // Clean mods
        ModOverlayEngine.CleanModifications(versionDir, manifest);

        // Mod files removed
        File.Exists(deployedModTexture).Should().BeFalse();
        File.Exists(deployedModFont).Should().BeFalse();

        // Base binary and base texture remain intact
        File.Exists(baseExe).Should().BeTrue();
        File.Exists(baseTexture).Should().BeTrue();
        File.ReadAllText(baseTexture).Should().Be("base_texture_data");
    }
}
