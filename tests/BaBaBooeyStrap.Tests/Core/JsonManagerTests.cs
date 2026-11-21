using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;
using BaBaBooeyStrap.Core.Storage;

namespace BaBaBooeyStrap.Tests.Core;

public class JsonManagerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _settingsFile;

    public JsonManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "BaBaBooeyStrap_JsonTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _settingsFile = Path.Combine(_tempDirectory, "Settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_InitializesDefaultAndSaves()
    {
        var manager = new JsonManager<Settings>(_settingsFile);
        File.Exists(_settingsFile).Should().BeFalse();

        manager.Load();

        manager.Loaded.Should().BeTrue();
        manager.Prop.Should().NotBeNull();
        manager.Prop.BootstrapperTitle.Should().Be("BaBaBooeyStrap");
        manager.Prop.MultiInstanceEnabled.Should().BeTrue();
        manager.Prop.MultiInstanceLaunchDelayMs.Should().Be(1500);

        // Verify that the file was written to disk
        File.Exists(_settingsFile).Should().BeTrue();
    }

    [Fact]
    public void Save_And_Load_PersistsCustomValues()
    {
        var manager = new JsonManager<Settings>(_settingsFile);
        manager.Load();

        manager.Prop.BootstrapperTitle = "Custom BaBaBooeyStrap Title";
        manager.Prop.Theme = Theme.Dark;
        manager.Prop.MultiInstanceLaunchDelayMs = 2500;
        manager.Prop.BootstrapperStyle = BootstrapperStyle.ByfronDialog;
        manager.Save();

        // Reload using a fresh manager instance
        var reloaded = new JsonManager<Settings>(_settingsFile);
        reloaded.Load();

        reloaded.Prop.BootstrapperTitle.Should().Be("Custom BaBaBooeyStrap Title");
        reloaded.Prop.Theme.Should().Be(Theme.Dark);
        reloaded.Prop.MultiInstanceLaunchDelayMs.Should().Be(2500);
        reloaded.Prop.BootstrapperStyle.Should().Be(BootstrapperStyle.ByfronDialog);
    }

    [Fact]
    public void Load_CorruptedFile_CreatesBackup_ResetsToDefaults_AndFiresEvent()
    {
        // Write corrupted JSON to the file
        File.WriteAllText(_settingsFile, "{ this is definitely not valid json : 123 }");

        var manager = new JsonManager<Settings>(_settingsFile);
        bool eventFired = false;
        string? backupPath = null;

        manager.OnLoadError += (sender, args) =>
        {
            eventFired = true;
            backupPath = args.BackupPath;
        };

        manager.Load();

        eventFired.Should().BeTrue();
        backupPath.Should().NotBeNullOrEmpty();
        File.Exists(backupPath!).Should().BeTrue();

        // Verify backup contains the corrupted text
        File.ReadAllText(backupPath!).Should().Contain("definitely not valid json");

        // Verify manager reset to valid defaults
        manager.Loaded.Should().BeTrue();
        manager.Prop.Should().NotBeNull();
        manager.Prop.BootstrapperTitle.Should().Be("BaBaBooeyStrap");
    }

    [Fact]
    public void HasFileOnDiskChanged_DetectsExternalModifications()
    {
        var manager = new JsonManager<Settings>(_settingsFile);
        manager.Load();
        manager.HasFileOnDiskChanged().Should().BeFalse();

        // Modify file on disk externally
        File.WriteAllText(_settingsFile, File.ReadAllText(_settingsFile) + "\n// modification comment");
        manager.HasFileOnDiskChanged().Should().BeTrue();
    }

    [Fact]
    public void ThreadSafe_ConcurrentSaves_DoNotThrowOrCorrupt()
    {
        var manager = new JsonManager<Settings>(_settingsFile);
        manager.Load();

        Parallel.For(0, 30, i =>
        {
            manager.Prop.MultiInstanceLaunchDelayMs = 1000 + i;
            manager.Save();
            var reloaded = new JsonManager<Settings>(_settingsFile);
            reloaded.Load();
        });

        // Ensure final file is valid JSON
        var finalCheck = new JsonManager<Settings>(_settingsFile);
        finalCheck.Load();
        finalCheck.Loaded.Should().BeTrue();
    }
}
