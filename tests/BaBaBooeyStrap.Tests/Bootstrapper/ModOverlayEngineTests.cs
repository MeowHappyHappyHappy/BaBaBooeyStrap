using System.Text;
using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Bootstrapper.Mods.Presets;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Bootstrapper;

public sealed class ModOverlayEngineTests : IDisposable
{
    private readonly ModOverlayEngine _engine = new();
    private readonly string _tempDirectory;
    private readonly string _modificationsDir;
    private readonly string _versionDir;

    public ModOverlayEngineTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ModOverlayTests_" + Guid.NewGuid().ToString("N"));
        _modificationsDir = Path.Combine(_tempDirectory, "Modifications");
        _versionDir = Path.Combine(_tempDirectory, "Versions", "version-test");

        Directory.CreateDirectory(_modificationsDir);
        Directory.CreateDirectory(_versionDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true); } catch { }
    }

    [Fact]
    public async Task ApplyModificationsAsync_MirrorsFilesAndExcludesSpecialFiles()
    {
        // Arrange
        // Valid mod file
        string textureDir = Path.Combine(_modificationsDir, "content", "textures");
        Directory.CreateDirectory(textureDir);
        File.WriteAllBytes(Path.Combine(textureDir, "custom.png"), new byte[] { 1, 2, 3 });

        // Excluded files: README.txt and .lock
        File.WriteAllText(Path.Combine(_modificationsDir, "README.txt"), "Help text");
        File.WriteAllText(Path.Combine(_modificationsDir, "temp.lock"), "Lock file");

        // Dangerous mesh file
        string headDir = Path.Combine(_modificationsDir, "content", "avatar", "heads");
        Directory.CreateDirectory(headDir);
        File.WriteAllText(Path.Combine(headDir, "head.mesh"), "DangerousMeshPayload");

        // Act
        var result = await _engine.ApplyModificationsAsync(_modificationsDir, _versionDir);

        // Assert
        result.Success.Should().BeTrue();
        result.ManifestedFiles.Should().Contain("content/textures/custom.png");
        result.ManifestedFiles.Should().NotContain("README.txt");
        result.ManifestedFiles.Should().NotContain("temp.lock");
        result.ManifestedFiles.Should().NotContain("content/avatar/heads/head.mesh");

        File.Exists(Path.Combine(_versionDir, "content", "textures", "custom.png")).Should().BeTrue();
        File.Exists(Path.Combine(_versionDir, "README.txt")).Should().BeFalse();
        File.Exists(Path.Combine(_versionDir, "temp.lock")).Should().BeFalse();
        File.Exists(Path.Combine(_versionDir, "content", "avatar", "heads", "head.mesh")).Should().BeFalse();

        // Default AppSettings.xml should have been generated
        File.Exists(Path.Combine(_versionDir, "AppSettings.xml")).Should().BeTrue();
    }

    [Fact]
    public async Task ApplyModificationsAsync_ModManifestReconciliation_CleansDeletedMods()
    {
        // Arrange: Previous run applied custom.png and old_mod.txt
        var previousManifest = new List<string> { "content/textures/custom.png", "old_mod.txt" };

        // In version dir, both files currently exist
        string destCustom = Path.Combine(_versionDir, "content", "textures", "custom.png");
        string destOld = Path.Combine(_versionDir, "old_mod.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(destCustom)!);
        File.WriteAllText(destCustom, "CustomTexture");
        File.WriteAllText(destOld, "OldModContent");

        // In modifications folder, user deleted old_mod.txt, only custom.png remains
        string srcCustom = Path.Combine(_modificationsDir, "content", "textures", "custom.png");
        Directory.CreateDirectory(Path.GetDirectoryName(srcCustom)!);
        File.WriteAllText(srcCustom, "CustomTexture");

        // Act
        var result = await _engine.ApplyModificationsAsync(_modificationsDir, _versionDir, previousManifest);

        // Assert: old_mod.txt was detected as removed and deleted
        result.Success.Should().BeTrue();
        File.Exists(destCustom).Should().BeTrue();
        File.Exists(destOld).Should().BeFalse();
    }

    [Fact]
    public void CursorPresetHandler_ApplyPresets_DeploysAndCleansCursors()
    {
        // Act: Apply 2006 preset
        CursorPresetHandler.ApplyCursorPreset(_modificationsDir, CursorPreset.Classic2006);

        // Assert: 2006 cursors deployed
        string arrow = Path.Combine(_modificationsDir, CursorPresetHandler.RelativeArrowCursor);
        string arrowFar = Path.Combine(_modificationsDir, CursorPresetHandler.RelativeArrowFarCursor);
        File.Exists(arrow).Should().BeTrue();
        File.Exists(arrowFar).Should().BeTrue();

        // Act: Reset to Default
        CursorPresetHandler.ApplyCursorPreset(_modificationsDir, CursorPreset.Default);

        // Assert: Cursors deleted to restore originals
        File.Exists(arrow).Should().BeFalse();
        File.Exists(arrowFar).Should().BeFalse();
    }

    [Fact]
    public void DeathSoundPresetHandler_ValidOgg_AppliesAndResetsDeathSound()
    {
        // Arrange: Valid Ogg header
        byte[] validOgg = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00 };
        byte[] invalidAudio = Encoding.UTF8.GetBytes("RIFF...WavAudioNotOgg");

        // Act & Assert: Invalid container format rejected
        Action badAct = () => DeathSoundPresetHandler.ApplyDeathSound(invalidAudio, _modificationsDir);
        badAct.Should().Throw<InvalidDataException>();

        // Act: Valid Ogg applied
        DeathSoundPresetHandler.ApplyDeathSound(validOgg, _modificationsDir);
        string oggFile = Path.Combine(_modificationsDir, DeathSoundPresetHandler.RelativeOuchPath);
        File.Exists(oggFile).Should().BeTrue();
        File.ReadAllBytes(oggFile).Should().BeEquivalentTo(validOgg);

        // Act: Reset
        DeathSoundPresetHandler.ResetDeathSound(_modificationsDir);
        File.Exists(oggFile).Should().BeFalse();
    }

    [Fact]
    public void AvatarBackgroundPresetHandler_SetOldAvatarBackground_EnablesAndDisables()
    {
        // Act: Enable
        AvatarBackgroundPresetHandler.SetOldAvatarBackground(true, _modificationsDir);
        string rbxlPath = Path.Combine(_modificationsDir, AvatarBackgroundPresetHandler.RelativeMobileRbxlPath);
        File.Exists(rbxlPath).Should().BeTrue();

        // Act: Disable
        AvatarBackgroundPresetHandler.SetOldAvatarBackground(false, _modificationsDir);
        File.Exists(rbxlPath).Should().BeFalse();
    }
}
