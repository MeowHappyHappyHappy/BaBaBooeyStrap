using System.Text;
using System.Text.Json;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Bootstrapper;

public sealed class FastFlagManagerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _settingsPath;

    public FastFlagManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "FFlagTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _settingsPath = Path.Combine(_tempDirectory, "ClientAppSettings.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true); } catch { }
    }

    [Fact]
    public void FastFlagManager_CrudOperations_SetsGetsAndRemovesFlagsCaseInsensitively()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);

        // Act
        manager.SetFlag("FFlagDebugDisplayFPS", true);
        manager.SetFlag("FIntTargetFps", 165);
        manager.SetFlag("FStringCustomMessage", "HelloRoblox");

        // Assert
        manager.Flags.Should().HaveCount(3);
        manager.GetFlag("fflagdebugdisplayfps").Should().Be(true);
        manager.GetFlag("FINTTARGETFPS").Should().Be(165);
        manager.GetFlag("FStringCustomMessage").Should().Be("HelloRoblox");

        // Act: Remove flag
        bool removed = manager.RemoveFlag("finttargetfps");

        // Assert
        removed.Should().BeTrue();
        manager.Flags.Should().NotContainKey("FIntTargetFps");
        manager.Flags.Should().HaveCount(2);
    }

    [Fact]
    public void SetFlag_NullOrWhitespaceKey_ThrowsArgumentException()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);

        // Act & Assert
        Action act1 = () => manager.SetFlag(null!, "val");
        Action act2 = () => manager.SetFlag("   ", "val");

        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SaveAndLoad_AtomicPersistence_WritesAndReloadsIndentedJson()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);
        manager.SetFlag("FFlagTestBool", true);
        manager.SetFlag("FIntTestInt", 240);

        // Act
        manager.Save();

        // Assert
        File.Exists(_settingsPath).Should().BeTrue();
        string rawJson = File.ReadAllText(_settingsPath, Encoding.UTF8);
        using (var doc = JsonDocument.Parse(rawJson))
        {
            doc.RootElement.GetProperty("FFlagTestBool").GetString().Should().Be("True");
            doc.RootElement.GetProperty("FIntTestInt").GetString().Should().Be("240");
        }

        // Reload in fresh manager
        var freshManager = new FastFlagManager(_settingsPath);
        freshManager.Load();

        freshManager.Flags.Should().ContainKey("FFlagTestBool");
        freshManager.Flags.Should().ContainKey("FIntTestInt");
    }

    [Fact]
    public void Load_CorruptedJson_CreatesBackupAndReinitializesCleanFlags()
    {
        // Arrange
        File.WriteAllText(_settingsPath, "{ Not valid json at all... broken syntax");
        var manager = new FastFlagManager(_settingsPath);

        // Act
        manager.Load();

        // Assert
        manager.Flags.Should().BeEmpty();
        var backups = Directory.GetFiles(_tempDirectory, "ClientAppSettings.json.corrupted.*.bak");
        backups.Length.Should().Be(1);
    }

    [Fact]
    public void Load_EnforcesAltEnterFullscreenPreset_RestoresToFalseIfMissingOrTrue()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);
        manager.SetFlag("FFlagHandleAltEnterFullscreenManually", "True");
        manager.Save();

        // Act
        var reloaded = new FastFlagManager(_settingsPath);
        reloaded.Load();

        // Assert: Restored to "False"
        reloaded.GetFlag("FFlagHandleAltEnterFullscreenManually")?.ToString().Should().Be("False");
    }

    [Fact]
    public void ApplyPreset_MsaaPreset_SetsFIntDebugForceMSAASamples()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);

        // Act
        manager.ApplyPreset("Rendering.MSAA", 4);

        // Assert
        manager.Flags.Should().ContainKey("FIntDebugForceMSAASamples");
        manager.Flags["FIntDebugForceMSAASamples"].Should().Be(4);
    }

    [Fact]
    public void ApplyPreset_VulkanAndD3D11_EnforcesMutualExclusion()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);

        // Act: Apply Vulkan
        manager.ApplyPreset("Rendering.Vulkan", "True");
        manager.Flags.Should().ContainKey("FFlagDebugGraphicsPreferVulkan");
        manager.Flags.Should().NotContainKey("FFlagDebugGraphicsPreferD3D11");

        // Act: Apply D3D11
        manager.ApplyPreset("Rendering.D3D11", "True");
        manager.Flags.Should().ContainKey("FFlagDebugGraphicsPreferD3D11");
        manager.Flags.Should().NotContainKey("FFlagDebugGraphicsPreferVulkan");
    }

    [Fact]
    public void ApplyPreset_FrmQualityOverride_ClampsValuesWithin1To21()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);

        // Act & Assert: Low value clamped to 1
        manager.ApplyPreset("Rendering.FRMQualityOverride", -5);
        manager.Flags["DFIntDebugFRMQualityLevelOverride"].Should().Be(1);

        // Act & Assert: High value clamped to 21
        manager.ApplyPreset("Rendering.FRMQualityOverride", 99);
        manager.Flags["DFIntDebugFRMQualityLevelOverride"].Should().Be(21);
    }

    [Fact]
    public void ApplyPreset_MeshLod_SetsStaticAndFourLevelFlags()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);

        // Act: Set LOD level 2
        manager.ApplyPreset("Geometry.MeshLOD", 2);

        // Assert
        manager.Flags["DFIntCSGLevelOfDetailSwitchingDistanceStatic"].Should().Be(2);
        manager.Flags.Should().ContainKey("DFIntCSGLevelOfDetailSwitchingDistance");
        manager.Flags.Should().ContainKey("DFIntCSGLevelOfDetailSwitchingDistanceL12");
        manager.Flags.Should().ContainKey("DFIntCSGLevelOfDetailSwitchingDistanceL23");
        manager.Flags.Should().ContainKey("DFIntCSGLevelOfDetailSwitchingDistanceL34");
    }

    [Fact]
    public void ImportJson_HandlesMissingBracesAndDuplicateKeys()
    {
        // Arrange: Missing braces and duplicate key
        string rawFragment = "\"FFlagDup\": \"FirstValue\", \"FFlagDup\": \"SecondValue\"";
        var manager = new FastFlagManager(_settingsPath);

        // Act
        manager.ImportJson(rawFragment);

        // Assert: Last write wins and missing braces normalized
        manager.Flags.Should().ContainKey("FFlagDup");
        manager.Flags["FFlagDup"].Should().Be("SecondValue");
    }

    [Fact]
    public void FastFlagManager_Utf8AndEmojiSupport_RoundTripsSafely()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);
        manager.SetFlag("FFlag測試旗標🚀", "🔥True🔥");
        manager.Save();

        // Act
        var reloaded = new FastFlagManager(_settingsPath);
        reloaded.Load();

        // Assert
        reloaded.Flags.Should().ContainKey("FFlag測試旗標🚀");
        reloaded.Flags["FFlag測試旗標🚀"].Should().Be("🔥True🔥");
    }

    [Fact]
    public void FastFlagManager_LargeNumbersAndStrings_DoesNotOverflow()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);
        string largeString = new string('X', 50000);
        long maxLong = long.MaxValue;

        manager.SetFlag("FStringLarge", largeString);
        manager.SetFlag("FIntMaxLong", maxLong);
        manager.Save();

        // Act
        var reloaded = new FastFlagManager(_settingsPath);
        reloaded.Load();

        // Assert
        reloaded.Flags["FStringLarge"].Should().Be(largeString);
        reloaded.Flags["FIntMaxLong"].Should().Be(maxLong);
    }

    [Fact]
    public void ImportJson_LeadingCommentsAndHeaders_ParsesCorrectly()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);
        string jsonWithHeaders = """
            // Bloxstrap FastFlags
            /* Pre-header multi-line block comment
               Second comment line */
            {
                "FFlagCommentTestA": true,
                "FIntTargetFps": 144,
                "FStringWelcome": "Hello Bloxstrap",
            }
            """;

        // Act
        manager.ImportJson(jsonWithHeaders);

        // Assert
        manager.Flags.Should().ContainKey("FFlagCommentTestA");
        manager.Flags["FFlagCommentTestA"].Should().Be(true);
        manager.Flags.Should().ContainKey("FIntTargetFps");
        manager.Flags["FIntTargetFps"].Should().Be(144L);
        manager.Flags.Should().ContainKey("FStringWelcome");
        manager.Flags["FStringWelcome"].Should().Be("Hello Bloxstrap");
    }

    [Fact]
    public void ImportJson_LeadingCommentsWithoutEnclosingBraces_ParsesCorrectly()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);
        string snippetWithComments = """
            // Bloxstrap Snippet
            "FFlagSnippetBool": false,
            "FIntSnippetInt": 120
            """;

        // Act
        manager.ImportJson(snippetWithComments);

        // Assert
        manager.Flags.Should().ContainKey("FFlagSnippetBool");
        manager.Flags["FFlagSnippetBool"].Should().Be(false);
        manager.Flags["FIntSnippetInt"].Should().Be(120L);
    }

    [Fact]
    public void ImportJson_UnbracedSnippetWithTrailingSingleLineComment_ParsesCorrectly()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);
        string snippet = "\"FFlagTrailingComment\": true // this is an inline comment";

        // Act
        manager.ImportJson(snippet);

        // Assert
        manager.Flags.Should().ContainKey("FFlagTrailingComment");
        manager.Flags["FFlagTrailingComment"].Should().Be(true);
    }

    [Fact]
    public void ImportJson_CommentOnlyInput_DoesNotThrow()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);
        string commentOnly = "// Bloxstrap comment only";

        // Act
        var act = () => manager.ImportJson(commentOnly);

        // Assert
        act.Should().NotThrow();
        manager.Flags.Should().BeEmpty();

        // Also verify multi-line comment only
        var actBlock = () => manager.ImportJson("/* Bloxstrap block comment only */");
        actBlock.Should().NotThrow();
        manager.Flags.Should().BeEmpty();
    }

    [Fact]
    public void ImportJson_MultipleUnbracedFlagsWithInlineComments_ParsesAllFlags()
    {
        // Arrange
        var manager = new FastFlagManager(_settingsPath);
        string multipleWithComments = """
            // Initial header comment
            "FFlagFirst": true, // inline comment on first flag
            "FIntSecond": 240, // inline comment on second flag
            "FStringThird": "UltraGraphics" // trailing comment on last flag
            """;

        // Act
        manager.ImportJson(multipleWithComments);

        // Assert
        manager.Flags.Should().ContainKey("FFlagFirst");
        manager.Flags["FFlagFirst"].Should().Be(true);
        manager.Flags.Should().ContainKey("FIntSecond");
        manager.Flags["FIntSecond"].Should().Be(240L);
        manager.Flags.Should().ContainKey("FStringThird");
        manager.Flags["FStringThird"].Should().Be("UltraGraphics");
    }
}
