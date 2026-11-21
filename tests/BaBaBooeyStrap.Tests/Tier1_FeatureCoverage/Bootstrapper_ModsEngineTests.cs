using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Bootstrapper.Mods.Presets;
using BaBaBooeyStrap.Tests.Mocks;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Bootstrapper_ModsEngineTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public Bootstrapper_ModsEngineTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    // =========================================================================
    // Feature 13: File Overlay Mods Engine
    // =========================================================================

    [Fact]
    public void F13_ModsEngine_MirrorModifications_CopiesFilesPreservingDirectoryTree()
    {
        // Arrange
        string modSubdir = Path.Combine(_env.ModificationsPath, "content", "textures");
        Directory.CreateDirectory(modSubdir);
        string modFile = Path.Combine(modSubdir, "custom.png");
        byte[] expectedBytes = new byte[] { 10, 20, 30, 40 };
        File.WriteAllBytes(modFile, expectedBytes);

        string versionDir = Path.Combine(_env.RobloxVersionsPath, "version-mods-test");
        Directory.CreateDirectory(versionDir);

        // Act
        var copied = ModOverlayEngine.ApplyModifications(_env.ModificationsPath, versionDir);

        // Assert
        string targetFile = Path.Combine(versionDir, "content", "textures", "custom.png");
        File.Exists(targetFile).Should().BeTrue();
        File.ReadAllBytes(targetFile).Should().BeEquivalentTo(expectedBytes);
        copied.Should().Contain("content/textures/custom.png");
    }

    [Fact]
    public void F13_ModsEngine_ModManifest_TracksCopiedFilesForCleanup()
    {
        // Arrange
        string modDir = Path.Combine(_env.RootPath, "mods_source");
        string versionDir = Path.Combine(_env.RootPath, "mods_target");
        Directory.CreateDirectory(Path.Combine(modDir, "a"));
        Directory.CreateDirectory(Path.Combine(modDir, "b"));
        File.WriteAllText(Path.Combine(modDir, "a", "1.txt"), "mod1");
        File.WriteAllText(Path.Combine(modDir, "b", "2.txt"), "mod2");

        // Act
        var manifest = ModOverlayEngine.ApplyModifications(modDir, versionDir);

        // Assert
        manifest.Count.Should().Be(2);
        manifest.Should().Contain("a/1.txt");
        manifest.Should().Contain("b/2.txt");
    }

    [Fact]
    public void F13_ModsEngine_CleanMods_RemovesOnlyManifestedFiles()
    {
        // Arrange
        string versionDir = Path.Combine(_env.RootPath, "mods_cleanup_test");
        Directory.CreateDirectory(versionDir);

        string nativeApp = Path.Combine(versionDir, "RobloxPlayerBeta.exe");
        File.WriteAllText(nativeApp, "NativeBinaryContent");

        string moddedFile = Path.Combine(versionDir, "modded_asset.png");
        File.WriteAllText(moddedFile, "ModdedAssetContent");

        // Act
        ModOverlayEngine.CleanModifications(versionDir, new[] { "modded_asset.png" });

        // Assert: modded file is deleted, native application remains intact
        File.Exists(moddedFile).Should().BeFalse();
        File.Exists(nativeApp).Should().BeTrue();
    }

    [Fact]
    public void F13_ModsEngine_EmptyModsFolder_DoesNotModifyVersionDirectory()
    {
        // Arrange
        string emptyMods = Path.Combine(_env.RootPath, "empty_mods");
        string versionDir = Path.Combine(_env.RootPath, "version_empty_test");
        Directory.CreateDirectory(emptyMods);
        Directory.CreateDirectory(versionDir);

        string dummyFile = Path.Combine(versionDir, "existing.txt");
        File.WriteAllText(dummyFile, "ExistingContent");
        var originalTime = File.GetLastWriteTimeUtc(dummyFile);

        // Act
        var manifest = ModOverlayEngine.ApplyModifications(emptyMods, versionDir);

        // Assert
        manifest.Should().BeEmpty();
        File.GetLastWriteTimeUtc(dummyFile).Should().Be(originalTime);
    }

    [Fact]
    public void F13_ModsEngine_OverwritesExistingFiles_WhenNewModProvided()
    {
        // Arrange
        string modDir = Path.Combine(_env.RootPath, "mods_overwrite_src");
        string versionDir = Path.Combine(_env.RootPath, "mods_overwrite_dst");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(versionDir);

        File.WriteAllBytes(Path.Combine(versionDir, "cursor.png"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(modDir, "cursor.png"), new byte[] { 9, 8, 7, 6, 5 });

        // Act
        ModOverlayEngine.ApplyModifications(modDir, versionDir);

        // Assert
        byte[] overwritten = File.ReadAllBytes(Path.Combine(versionDir, "cursor.png"));
        overwritten.Length.Should().Be(5);
        overwritten[0].Should().Be(9);
    }

    // =========================================================================
    // Feature 14: Font / Cursor / Audio Mods
    // =========================================================================

    [Fact]
    public void F14_FontValidation_TrueTypeMagicBytes_ValidatesValidTtfHeader()
    {
        // Arrange: Valid TrueType magic bytes (00 01 00 00) followed by dummy font tables
        byte[] ttfBytes = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x80 };

        // Act
        bool isValid = FontValidator.IsValidFont(ttfBytes);

        // Assert
        isValid.Should().BeTrue();
    }

    [Fact]
    public void F14_FontValidation_InvalidMagicBytes_RejectsCorruptFontFile()
    {
        // Arrange: Plain text ASCII instead of font binary
        byte[] corruptBytes = Encoding.UTF8.GetBytes("Not A TrueType Font Header");

        // Act
        bool isValid = FontValidator.IsValidFont(corruptBytes);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void F14_CursorPresets_Classic2006_DeploysArrowAndBeamTextures()
    {
        // Arrange
        string versionDir = Path.Combine(_env.RootPath, "version_cursor_2006");
        Directory.CreateDirectory(versionDir);

        // Act
        CursorPresetHandler.ApplyCursorPreset(versionDir, CursorPreset.Classic2006);

        // Assert
        string cursorDir = Path.Combine(versionDir, "content", "textures", "Cursors", "KeyboardMouse");
        File.Exists(Path.Combine(cursorDir, "ArrowCursor.png")).Should().BeTrue();
        File.Exists(Path.Combine(cursorDir, "ArrowFarCursor.png")).Should().BeTrue();
    }

    [Fact]
    public void F14_CursorPresets_Classic2013_Deploys2013Textures()
    {
        // Arrange
        string versionDir = Path.Combine(_env.RootPath, "version_cursor_2013");
        Directory.CreateDirectory(versionDir);

        // Act
        CursorPresetHandler.ApplyCursorPreset(versionDir, CursorPreset.Classic2013);

        // Assert
        string cursorDir = Path.Combine(versionDir, "content", "textures", "Cursors", "KeyboardMouse");
        File.Exists(Path.Combine(cursorDir, "ArrowCursor.png")).Should().BeTrue();
    }

    [Fact]
    public void F14_AudioMods_CustomDeathSound_CopiesOggToSoundsDirectory()
    {
        // Arrange
        string versionDir = Path.Combine(_env.RootPath, "version_audio_test");
        Directory.CreateDirectory(versionDir);

        // Valid Ogg header: 'O', 'g', 'g', 'S'
        byte[] validOgg = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00 };

        // Act
        DeathSoundPresetHandler.ApplyDeathSound(validOgg, versionDir);

        // Assert
        string oggPath = Path.Combine(versionDir, "content", "sounds", "ouch.ogg");
        File.Exists(oggPath).Should().BeTrue();
        File.ReadAllBytes(oggPath).Should().BeEquivalentTo(validOgg);
    }
}
