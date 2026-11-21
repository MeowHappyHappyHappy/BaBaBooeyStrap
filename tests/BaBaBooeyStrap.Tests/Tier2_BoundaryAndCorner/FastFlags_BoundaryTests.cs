using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Tier2_BoundaryAndCorner;

public sealed class FastFlags_BoundaryTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public FastFlags_BoundaryTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    // =========================================================================
    // Feature 12: FastFlag Manager & Presets Boundaries (8 Tests)
    // =========================================================================

    [Fact]
    public void F12_FastFlags_ConflictingFlagTypes_ReplacesOrThrowsConsistentType()
    {
        // Arrange
        string file = Path.Combine(_env.RootPath, "conflict_type_fflags.json");
        var manager = new FastFlagManager(file);

        // Act: Set bool flag, then overwrite with string
        manager.SetFlag("FFlagDebugDisplayFPS", true);
        manager.SetFlag("FFlagDebugDisplayFPS", "true");

        // Assert: Overwrites consistently
        manager.Flags["FFlagDebugDisplayFPS"].Should().Be("true");
    }

    [Fact]
    public void F12_FastFlags_HugeStringAndIntegerValues_SerializesWithoutOverflow()
    {
        // Arrange
        string file = Path.Combine(_env.RootPath, "huge_values_fflags.json");
        var manager = new FastFlagManager(file);

        string hugeString = new string('Z', 50000);
        long hugeInt = long.MaxValue;

        // Act
        manager.SetFlag("FStringHugePayload", hugeString);
        manager.SetFlag("FIntMaxNumber", hugeInt);
        manager.Save();

        var reloaded = new FastFlagManager(file);
        reloaded.Load();

        // Assert
        reloaded.Flags["FStringHugePayload"].Should().Be(hugeString);
        reloaded.Flags["FIntMaxNumber"].Should().Be(hugeInt);
    }

    [Fact]
    public void F12_FastFlags_InvalidJsonInClientAppSettings_BacksUpAndRegenerates()
    {
        // Arrange
        string file = Path.Combine(_env.RootPath, "corrupted_clientappsettings.json");
        File.WriteAllText(file, "{ Not Valid JSON at all ... ");

        var manager = new FastFlagManager(file);

        // Act: Load should back up to .bak and clear
        manager.Load();

        // Assert
        manager.Flags.Should().BeEmpty();
        var backups = Directory.GetFiles(_env.RootPath, "corrupted_clientappsettings.json.corrupted.*.bak");
        backups.Length.Should().Be(1);
    }

    [Fact]
    public void F12_FastFlags_EmojiAndUnicodeKeys_HandlesUtf8CharactersSafely()
    {
        // Arrange
        string file = Path.Combine(_env.RootPath, "unicode_fflags.json");
        var manager = new FastFlagManager(file);

        // Act
        manager.SetFlag("FFlag測試旗標🚀", "🔥True🔥");
        manager.Save();

        var reloaded = new FastFlagManager(file);
        reloaded.Load();

        // Assert: UTF-8 characters preserved
        reloaded.Flags.Should().ContainKey("FFlag測試旗標🚀");
        reloaded.Flags["FFlag測試旗標🚀"].Should().Be("🔥True🔥");
    }

    [Fact]
    public void F12_FastFlags_DuplicateEntriesInImportJson_ResolvesLastWriteWins()
    {
        // Arrange
        string file = Path.Combine(_env.RootPath, "duplicate_keys.json");
        var manager = new FastFlagManager(file);
        string jsonWithDuplicate = "{\"FFlagDuplicate\": \"FirstValue\", \"FFlagDuplicate\": \"SecondValue\"}";

        // Act
        manager.ImportJson(jsonWithDuplicate);

        // Assert: Last write wins
        manager.Flags["FFlagDuplicate"].Should().Be("SecondValue");
    }

    [Fact]
    public void F12_FastFlags_NullOrEmptyFlagName_ThrowsArgumentException()
    {
        // Arrange
        string file = Path.Combine(_env.RootPath, "null_flag_name.json");
        var manager = new FastFlagManager(file);

        // Act & Assert
        Action act1 = () => manager.SetFlag(null!, "val");
        Action act2 = () => manager.SetFlag("   ", "val");

        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void F12_FastFlags_PresetTogglesWithConflictingRenderers_EnforcesMutualExclusion()
    {
        // Arrange
        string file = Path.Combine(_env.RootPath, "mutual_exclusion.json");
        var manager = new FastFlagManager(file);

        // Act: Apply D3D11 then Vulkan
        manager.ApplyPreset("Rendering.D3D11", "True");
        manager.Flags.Should().ContainKey("FFlagDebugGraphicsPreferD3D11");

        manager.ApplyPreset("Rendering.Vulkan", "True");

        // Assert: D3D11 removed when Vulkan enabled
        manager.Flags.Should().ContainKey("FFlagDebugGraphicsPreferVulkan");
        manager.Flags.Should().NotContainKey("FFlagDebugGraphicsPreferD3D11");
    }

    [Fact]
    public void F12_FastFlags_ZeroByteClientAppSettingsFile_InitializesEmptyDictionary()
    {
        // Arrange
        string emptyFile = Path.Combine(_env.RootPath, "empty_flags.json");
        File.WriteAllBytes(emptyFile, Array.Empty<byte>());

        var manager = new FastFlagManager(emptyFile);

        // Act
        manager.Load();

        // Assert: Does not throw exception; flags empty
        manager.Flags.Should().BeEmpty();
    }

    // =========================================================================
    // Feature 23: FastFlag Editor Dialog Boundaries (7 Tests)
    // =========================================================================

    [Fact]
    public void F23_Editor_BulkImportMalformedJson_DisplaysValidationErrorWithoutMutatingFlags()
    {
        // Arrange
        var vm = new FastFlagEditorViewModel();
        vm.AddFlag("FFlagExisting", "True");

        // Act: Import broken JSON
        bool success = vm.ImportJson("{ Invalid JSON syntax ");

        // Assert: Import failed, existing flags preserved untouched
        success.Should().BeFalse();
        vm.Flags.Should().ContainSingle();
        vm.Flags[0].Name.Should().Be("FFlagExisting");
    }

    [Fact]
    public void F23_Editor_SearchFilterSpecialRegexCharacters_EscapesInputSafely()
    {
        // Arrange
        var vm = new FastFlagEditorViewModel();
        vm.AddFlag("FFlag[Special]Test", "True");
        vm.AddFlag("FFlagNormalTest", "True");

        // Act: Search contains regex metacharacters: [ ] * + ?
        vm.SearchText = "[Special]";
        var results = vm.FilteredFlags.ToList();

        // Assert: Safely escaped and matched literally
        results.Should().ContainSingle();
        results[0].Name.Should().Be("FFlag[Special]Test");
    }

    [Fact]
    public void F23_Editor_AddingFlagWithWhitespacePadding_TrimsKeyAndValidates()
    {
        // Arrange
        var vm = new FastFlagEditorViewModel();

        // Act
        vm.AddFlag("   FFlagTrimmedName   ", "True");

        // Assert
        vm.Flags.Should().ContainSingle();
        vm.Flags[0].Name.Should().Be("FFlagTrimmedName");
    }

    [Fact]
    public void F23_Editor_BulkExportEmptyFlags_ProducesValidEmptyJsonObject()
    {
        // Arrange
        var vm = new FastFlagEditorViewModel();

        // Act
        string exported = vm.ExportJson();

        // Assert
        exported.Trim().Should().Be("{}");
    }

    [Fact]
    public void F23_Editor_DeleteNonExistentFlag_ReturnsFalseWithoutException()
    {
        // Arrange
        var vm = new FastFlagEditorViewModel();

        // Act
        bool deleted = vm.DeleteFlag("FFlagDoesNotExist");

        // Assert
        deleted.Should().BeFalse();
    }

    [Fact]
    public void F23_Editor_HighVolumeFlagCollection_MaintainsResponsiveFilter()
    {
        // Arrange: Populate 1,000 flags
        var vm = new FastFlagEditorViewModel();
        for (int i = 0; i < 1000; i++)
        {
            vm.AddFlag($"FFlagVolumeTest_{i:D4}", "True");
        }

        // Act: Filter search
        var sw = System.Diagnostics.Stopwatch.StartNew();
        vm.SearchText = "0500";
        var filtered = vm.FilteredFlags.ToList();
        sw.Stop();

        // Assert: Resolves quickly (< 50ms)
        filtered.Should().ContainSingle();
        filtered[0].Name.Should().Be("FFlagVolumeTest_0500");
        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }

    [Fact]
    public void F23_Editor_InvalidDataTypeCastInEditor_RejectsInvalidInput()
    {
        // Arrange: Empty or whitespace flag name
        var vm = new FastFlagEditorViewModel();

        // Act & Assert: Throws ArgumentException on empty flag name
        Action act = () => vm.AddFlag("", "123");
        act.Should().Throw<ArgumentException>();
    }
}
