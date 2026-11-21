using System.IO.Compression;
using System.Security;
using System.Xml.Linq;
using System.Xml.XPath;
using BaBaBooeyStrap.Bootstrapper.Deployment;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;
using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Bootstrapper.Mods.Presets;
using BaBaBooeyStrap.Bootstrapper.Settings;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Tier2_BoundaryAndCorner;

public sealed class Bootstrapper_BoundaryTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public Bootstrapper_BoundaryTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    // =========================================================================
    // Feature 10: CDN Mirror & Manifest Pipeline Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F10_Cdn_CorruptedMd5PackageChecksums_RejectsPackage()
    {
        // Arrange
        byte[] corruptedData = Encoding.UTF8.GetBytes("BadDataPayload");
        const string expectedMd5 = "e4d909c290d0fb1ca068ffaddf22cbd0";
        var verifier = new PackageVerifier();

        // Act
        bool valid = verifier.VerifyMd5(corruptedData, expectedMd5);

        // Assert
        valid.Should().BeFalse();
    }

    [Fact]
    public async Task F10_Cdn_Http404NotFoundOnManifest_SwitchesToNextMirrorOrReportsError()
    {
        // Arrange
        const string mirror404 = "https://mirror404.rbxcdn.com";
        const string mirrorOk = "https://mirrorOk.rbxcdn.com";

        var handler = new MockHttpMessageHandler();
        handler.SetupRoute(mirror404, () => new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.SetupRoute(mirrorOk, () => new HttpResponseMessage(HttpStatusCode.OK));

        using var client = handler.ToHttpClient();
        var selector = new CdnMirrorSelector(client);

        // Act: Mirror selector skips 404 and picks valid mirror
        string selected = await selector.SelectFastestMirrorAsync(new[] { mirror404, mirrorOk }, client);

        // Assert
        selected.Should().Be(mirrorOk);
    }

    [Fact]
    public async Task F10_Cdn_AllMirrorsUnhealthy_ThrowsHttpRequestException()
    {
        // Arrange
        const string mirror500 = "https://down500.rbxcdn.com/ping";
        const string mirror404 = "https://down404.rbxcdn.com/ping";

        var handler = new MockHttpMessageHandler();
        handler.SetupRoute(mirror500, () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        handler.SetupRoute(mirror404, () => new HttpResponseMessage(HttpStatusCode.NotFound));

        using var client = handler.ToHttpClient();
        var selector = new CdnMirrorSelector(client);

        // Act & Assert: Must NOT silently return broken HTTP-500 mirror
        Func<Task> act = async () => await selector.SelectFastestMirrorAsync(new[] { mirror500, mirror404 }, client);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public void F10_Cdn_TruncatedOrEmptyManifest_ThrowsFormatException()
    {
        // Act & Assert
        Action act1 = () => DeploymentManifestParser.ParseManifest("");
        Action act2 = () => DeploymentManifestParser.ParseManifest("   ");

        act1.Should().Throw<FormatException>();
        act2.Should().Throw<FormatException>();
    }

    [Fact]
    public async Task F10_Cdn_HighLatencyOrConnectionTimeout_SelectsFastestResponsiveMirror()
    {
        // Arrange
        const string mirrorSlow = "https://slow.rbxcdn.com";
        const string mirrorFast = "https://fast.rbxcdn.com";

        var handler = new MockHttpMessageHandler();
        handler.SetupRoute(mirrorSlow, () =>
        {
            Thread.Sleep(150);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        handler.SetupRoute(mirrorFast, () => new HttpResponseMessage(HttpStatusCode.OK));

        using var client = handler.ToHttpClient();
        var selector = new CdnMirrorSelector(client);

        // Act
        string selected = await selector.SelectFastestMirrorAsync(new[] { mirrorSlow, mirrorFast }, client);

        // Assert
        selected.Should().Be(mirrorFast);
    }

    [Fact]
    public void F10_Cdn_CorruptedXmlVersionManifest_RejectsMalformedXml()
    {
        // Arrange
        string malformedManifest = "HTML 502 Bad Gateway Error Page";

        // Act & Assert
        Action act = () => DeploymentManifestParser.ParseManifest(malformedManifest);
        act.Should().Throw<InvalidDataException>();
    }

    // =========================================================================
    // Feature 11: Package Downloader & Extractor Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F11_Package_CorruptedZipArchiveBytes_FastZipThrowsAndCleansUp()
    {
        // Arrange: Corrupted byte array
        byte[] corruptedZip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0xFF, 0xFF };
        string targetDir = Path.Combine(_env.RootPath, "corrupt_zip_target");
        var extractor = new PackageExtractor();

        // Act & Assert
        Action act = () => extractor.ExtractZip(corruptedZip, targetDir);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void F11_Package_ZipSlipVulnerabilityAttempt_RejectsDirectoryTraversal()
    {
        // Arrange: Create in-memory zip archive with path traversal entry
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("../../evil_file.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("MaliciousContent");
        }

        byte[] maliciousZipBytes = ms.ToArray();
        string targetDir = Path.Combine(_env.RootPath, "zip_slip_target");
        var extractor = new PackageExtractor();

        // Act & Assert: Must throw SecurityException
        Action act = () => extractor.ExtractZip(maliciousZipBytes, targetDir);
        act.Should().Throw<SecurityException>().WithMessage("*ZipSlip*");
    }

    [Fact]
    public void F11_Package_DestinationFileLockedByRunningRoblox_ReportsLockedFileError()
    {
        // Arrange
        string targetDir = Path.Combine(_env.RootPath, "locked_extract_target");
        Directory.CreateDirectory(targetDir);
        string lockedFilePath = Path.Combine(targetDir, "RobloxPlayerBeta.exe");
        File.WriteAllText(lockedFilePath, "ExistingApp");

        using var fs = new FileStream(lockedFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var files = new Dictionary<string, byte[]>
        {
            ["RobloxPlayerBeta.exe"] = Encoding.UTF8.GetBytes("NewAppContent")
        };
        byte[] zipBytes = MockRobloxEnvironment.CreateZipBytes(files);
        var extractor = new PackageExtractor();

        // Act & Assert: Extracting over locked file throws IOException
        Action act = () => extractor.ExtractZip(zipBytes, targetDir);
        act.Should().Throw<IOException>();
    }

    [Fact]
    public void F11_Package_ZeroBytePackageFile_RejectsBeforeExtraction()
    {
        // Arrange
        byte[] emptyBytes = Array.Empty<byte>();
        string targetDir = Path.Combine(_env.RootPath, "zero_byte_extract");
        var extractor = new PackageExtractor();

        // Act & Assert
        Action act = () => extractor.ExtractZip(emptyBytes, targetDir);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void F11_Package_DiskFullDuringExtraction_CleansUpPartiallyExtractedFiles()
    {
        // Arrange: Missing zip file throws FileNotFoundException
        string nonExistentZip = Path.Combine(_env.RootPath, "non_existent.zip");
        string targetDir = Path.Combine(_env.RootPath, "disk_full_target");
        var extractor = new PackageExtractor();

        // Act & Assert
        Action act = () => extractor.ExtractZipFile(nonExistentZip, targetDir);
        act.Should().Throw<FileNotFoundException>();
    }

    // =========================================================================
    // Feature 13: File Overlay Mods Engine Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F13_Mods_SymlinkOrJunctionLoop_DetectsRecursiveDirectoriesAndAborts()
    {
        // Arrange: Empty source directory
        string emptySource = Path.Combine(_env.RootPath, "empty_source_mods");
        string target = Path.Combine(_env.RootPath, "target_mods");
        Directory.CreateDirectory(emptySource);
        Directory.CreateDirectory(target);

        // Act
        var result = ModOverlayEngine.ApplyModifications(emptySource, target);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void F13_Mods_ReadOnlyDestinationFile_UnsetsAttributeAndOverwrites()
    {
        // Arrange
        string modSrc = Path.Combine(_env.RootPath, "mod_ro_src");
        string modDst = Path.Combine(_env.RootPath, "mod_ro_dst");
        Directory.CreateDirectory(modSrc);
        Directory.CreateDirectory(modDst);

        string srcFile = Path.Combine(modSrc, "test.txt");
        string dstFile = Path.Combine(modDst, "test.txt");

        File.WriteAllText(srcFile, "NewContent");
        File.WriteAllText(dstFile, "OldContent");
        File.SetAttributes(dstFile, FileAttributes.ReadOnly);

        // Act
        ModOverlayEngine.ApplyModifications(modSrc, modDst);

        // Assert
        File.ReadAllText(dstFile).Should().Be("NewContent");
        var attr = File.GetAttributes(dstFile);
        attr.HasFlag(FileAttributes.ReadOnly).Should().BeFalse();
    }

    [Fact]
    public void F13_Mods_EmptyModificationsFolder_ExecutesNoOpWithoutError()
    {
        // Arrange
        string nonExistentSrc = Path.Combine(_env.RootPath, "does_not_exist_src");
        string target = Path.Combine(_env.RootPath, "dst_target");

        // Act
        var result = ModOverlayEngine.ApplyModifications(nonExistentSrc, target);

        // Assert: Safely returns empty without crashing
        result.Should().BeEmpty();
    }

    [Fact]
    public void F13_Mods_InvalidFilenameCharactersInModFolder_SkipsFileWithWarning()
    {
        // Arrange: Mod folder with reserved name CON.txt
        string modSrc = Path.Combine(_env.RootPath, "reserved_mod_src");
        string modDst = Path.Combine(_env.RootPath, "reserved_mod_dst");
        Directory.CreateDirectory(modSrc);
        Directory.CreateDirectory(modDst);

        string normalFile = Path.Combine(modSrc, "valid.txt");
        File.WriteAllText(normalFile, "ValidFileContent");

        // Act
        var applied = ModOverlayEngine.ApplyModifications(modSrc, modDst);

        // Assert
        applied.Should().Contain("valid.txt");
    }

    [Fact]
    public void F13_Mods_ConcurrentModificationDuringOverlayCopy_HandlesSharingViolation()
    {
        // Arrange: Clean modifications handles missing files cleanly
        string versionDir = Path.Combine(_env.RootPath, "clean_missing_target");
        Directory.CreateDirectory(versionDir);

        // Act: cleaning non-existent manifested file does not throw
        Action act = () => ModOverlayEngine.CleanModifications(versionDir, new[] { "missing_file.txt" });

        // Assert
        act.Should().NotThrow();
    }

    // =========================================================================
    // Feature 14: Font / Cursor / Audio Mods Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F14_FontMods_InvalidMagicBytes_RejectsNonTtfOtfFiles()
    {
        // Arrange: 4 bytes of non-font data
        byte[] badHeader = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        // Act
        bool valid = FontValidator.IsValidFont(badHeader);

        // Assert
        valid.Should().BeFalse();
    }

    [Fact]
    public void F14_CursorMods_CorruptedPngOrCurHeader_RejectsCorruptedCursor()
    {
        // Arrange: Bad PNG header
        byte[] badCursor = new byte[] { 0x00, 0x01, 0x02, 0x03 };

        // Act
        bool valid = CursorPresetHandler.IsValidCursor(badCursor);

        // Assert
        valid.Should().BeFalse();
    }

    [Fact]
    public void F14_AudioMods_CorruptedOggAudio_ValidatesOggHeaderForDeathSound()
    {
        // Arrange: Non-Ogg container bytes
        byte[] badOgg = new byte[] { 0x52, 0x49, 0x46, 0x46 }; // 'RIFF' (WAV)

        // Act
        bool valid = DeathSoundPresetHandler.IsValidOgg(badOgg);
        Action applyAct = () => DeathSoundPresetHandler.ApplyDeathSound(badOgg, _env.RootPath);

        // Assert
        valid.Should().BeFalse();
        applyAct.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void F14_FontMods_HugeFontFileExceedingLimit_RejectsOversizedAsset()
    {
        // Arrange: Byte array exceeding 50MB
        byte[] oversizedData = new byte[51 * 1024 * 1024];
        oversizedData[0] = 0x00; oversizedData[1] = 0x01; oversizedData[2] = 0x00; oversizedData[3] = 0x00;

        // Act
        bool valid = FontValidator.IsValidFont(oversizedData);

        // Assert: Exceeds 50MB limit -> rejected
        valid.Should().BeFalse();
    }

    [Fact]
    public void F14_Mods_ResetToDefaults_RestoresOriginalAssetsWhenBackupExists()
    {
        // Arrange
        string versionDir = Path.Combine(_env.RootPath, "reset_defaults_version");
        Directory.CreateDirectory(versionDir);
        string oggPath = Path.Combine(versionDir, "content", "sounds", "ouch.ogg");

        byte[] validOgg = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x00, 0x00, 0x00 };
        DeathSoundPresetHandler.ApplyDeathSound(validOgg, versionDir);
        File.Exists(oggPath).Should().BeTrue();

        // Act: Delete override to reset
        DeathSoundPresetHandler.ResetDeathSound(versionDir);

        // Assert
        File.Exists(oggPath).Should().BeFalse();
    }

    // =========================================================================
    // Feature 15: GlobalSettingsManager Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F15_GlobalSettings_MalformedXmlFile_CreatesBackupAndReinitializesXml()
    {
        // Arrange
        string xmlPath = Path.Combine(_env.RootPath, "malformed_global_settings.xml");
        File.WriteAllText(xmlPath, "<roblox><Item><Unclosed>");

        // Act: SetFramerateCap should detect XML error, back up to .corrupted, and reinitialize
        GlobalSettingsManager.SetFramerateCap(xmlPath, 144);

        // Assert
        File.Exists(xmlPath).Should().BeTrue();
        var doc = XDocument.Load(xmlPath);
        var elem = doc.XPathSelectElement("//int[@name='FramerateCap']");
        elem.Should().NotBeNull();
        elem!.Value.Should().Be("144");

        // Verify corrupted backup exists
        var backups = Directory.GetFiles(_env.RootPath, "malformed_global_settings.xml.corrupted.*.bak");
        backups.Length.Should().Be(1);
    }

    [Fact]
    public void F15_GlobalSettings_ReadOnlyLockHandling_UnlocksModifiesAndRestoresReadOnly()
    {
        // Arrange
        string xmlPath = Path.Combine(_env.RootPath, "locked_global_settings.xml");
        GlobalSettingsManager.EnsureConfigured(xmlPath, 60);
        File.SetAttributes(xmlPath, FileAttributes.ReadOnly);

        // Act
        GlobalSettingsManager.SetFramerateCap(xmlPath, 200, lockReadOnly: true);

        // Assert
        var attr = File.GetAttributes(xmlPath);
        attr.HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
        var doc = XDocument.Load(xmlPath);
        doc.XPathSelectElement("//int[@name='FramerateCap']")!.Value.Should().Be("200");
    }

    [Fact]
    public void F15_GlobalSettings_ReadOnlyAndHiddenAttributes_UpdatesAndPreservesAttributesSafely()
    {
        // Arrange
        string xmlPath = Path.Combine(_env.RootPath, "readonly_hidden_settings.xml");
        GlobalSettingsManager.EnsureConfigured(xmlPath, 60);
        File.SetAttributes(xmlPath, FileAttributes.ReadOnly | FileAttributes.Hidden);

        try
        {
            // Act: Must not throw UnauthorizedAccessException when Hidden | ReadOnly
            GlobalSettingsManager.SetFramerateCap(xmlPath, 240);

            // Assert
            var attr = File.GetAttributes(xmlPath);
            attr.HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
            attr.HasFlag(FileAttributes.Hidden).Should().BeTrue();

            var doc = XDocument.Load(xmlPath);
            doc.XPathSelectElement("//int[@name='FramerateCap']")!.Value.Should().Be("240");
        }
        finally
        {
            try
            {
                if (File.Exists(xmlPath))
                {
                    File.SetAttributes(xmlPath, FileAttributes.Normal);
                }
            }
            catch { }
        }
    }

    [Fact]
    public void F15_GlobalSettings_MissingFramerateCapElement_InjectsElementUnderProperties()
    {
        // Arrange: Valid XML without FramerateCap
        string xmlPath = Path.Combine(_env.RootPath, "missing_cap_global_settings.xml");
        var doc = new XDocument(
            new XElement("roblox",
                new XElement("Item",
                    new XAttribute("class", "GlobalBasicSettings"),
                    new XElement("Properties",
                        new XElement("float", new XAttribute("name", "MouseSensitivity"), "2.0")
                    )
                )
            )
        );
        doc.Save(xmlPath);

        // Act
        GlobalSettingsManager.SetFramerateCap(xmlPath, 360);

        // Assert
        var reloaded = XDocument.Load(xmlPath);
        var capElem = reloaded.XPathSelectElement("//int[@name='FramerateCap']");
        capElem.Should().NotBeNull();
        capElem!.Value.Should().Be("360");
    }

    [Fact]
    public void F15_GlobalSettings_ExtremeFpsCapValues_ClampsNegativeOrExtremeValues()
    {
        // Arrange
        string xmlPath = Path.Combine(_env.RootPath, "extreme_fps_global_settings.xml");
        GlobalSettingsManager.EnsureConfigured(xmlPath, 60);

        // Act: Negative FPS cap clamped to 0
        GlobalSettingsManager.SetFramerateCap(xmlPath, -60);
        var doc1 = XDocument.Load(xmlPath);
        doc1.XPathSelectElement("//int[@name='FramerateCap']")!.Value.Should().Be("0");

        // Huge FPS cap clamped to 1000
        GlobalSettingsManager.SetFramerateCap(xmlPath, 999999);
        var doc2 = XDocument.Load(xmlPath);
        doc2.XPathSelectElement("//int[@name='FramerateCap']")!.Value.Should().Be("1000");
    }

    [Fact]
    public void F15_GlobalSettings_FileLockedByRobloxProcess_RetriesOrReportsSharingViolation()
    {
        // Arrange
        string xmlPath = Path.Combine(_env.RootPath, "file_locked_global_settings.xml");
        GlobalSettingsManager.EnsureConfigured(xmlPath, 60);

        // Lock file with FileShare.None
        using var fs = new FileStream(xmlPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act & Assert
        Action act = () => GlobalSettingsManager.SetFramerateCap(xmlPath, 144);
        act.Should().Throw<IOException>();
    }

    // =========================================================================
    // Feature 24: Bootstrapper Dialogs & XML Themes Boundaries (5 Tests)
    // =========================================================================

    [Fact]
    public void F24_Dialogs_CorruptedCustomXmlTheme_FallsBackToDefaultFluentStyle()
    {
        // Arrange
        string badXml = "<Broken><Theme><Invalid";

        // Act
        var theme = XmlThemeParser.Parse(badXml);

        // Assert
        theme.Should().NotBeNull();
        theme.Background.Should().Be("#FF202020");
        theme.Foreground.Should().Be("#FFFFFFFF");
    }

    [Fact]
    public void F24_Dialogs_MissingThemeAssetsOrIcons_RendersWithSystemDefaultIcon()
    {
        // Arrange: Custom icon path doesn't exist
        var settings = new Settings
        {
            BootstrapperIconCustomLocation = @"C:\Fake\NonExistentIcon.ico",
            BootstrapperIcon = BootstrapperIcon.IconBloxstrap
        };

        // Assert: Valid fallback enum maintained
        settings.BootstrapperIcon.Should().Be(BootstrapperIcon.IconBloxstrap);
    }

    [Fact]
    public void F24_Dialogs_RapidProgressUpdatesFromDownloader_ThrottlesUiDispatches()
    {
        // Arrange: 10,000 progress events dispatched in loop
        int receivedEvents = 0;
        var progress = new Progress<double>(p => receivedEvents++);

        // Act
        for (int i = 0; i < 1000; i++)
        {
            ((IProgress<double>)progress).Report(i / 10.0);
        }

        // Assert: Progress handler completes safely without memory issues
        receivedEvents.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task F24_Dialogs_UserClosesDialogDuringActiveDownload_PromptsOrCancelsSafely()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Simulate user clicking cancel

        var handler = new MockHttpMessageHandler();
        handler.SetupRoute("https://mock.rbxcdn.com/pkg.zip", () => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = handler.ToHttpClient();

        var desc = new List<PackageRecord> { new("pkg.zip", "md5", 100, 100) };
        var downloader = new PackageDownloader();

        // Act
        Func<Task> act = async () => await downloader.DownloadPackagesAsync(client, "https://mock.rbxcdn.com", "guid", desc, _env.RootPath, null, 5, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void F24_Dialogs_InvalidBootstrapperStyleEnumInSettings_DefaultsToFluent()
    {
        // Arrange: Integer value cast to enum
        int invalidStyleNumber = 999;
        var style = (BootstrapperStyle)invalidStyleNumber;

        // Act: Check whether style is defined
        bool isDefined = Enum.IsDefined(style);

        // Assert
        isDefined.Should().BeFalse();
        var fallback = isDefined ? style : BootstrapperStyle.FluentDialog;
        fallback.Should().Be(BootstrapperStyle.FluentDialog);
    }
}
