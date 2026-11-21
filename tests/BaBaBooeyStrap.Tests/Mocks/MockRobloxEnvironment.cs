using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace BaBaBooeyStrap.Tests.Mocks;

/// <summary>
/// Creates an isolated temporary directory sandbox for testing BaBaBooeyStrap filesystem interactions,
/// including Roblox version installations, log file streaming, FastFlag modifications, and XML configurations.
/// Fully cleans up temporary artifacts on Dispose().
/// </summary>
public sealed class MockRobloxEnvironment : IDisposable
{
    public string RootPath { get; }
    public string BaBaBooeyStrapPath { get; }
    public string RobloxPath { get; }
    public string RobloxLogsPath { get; }
    public string RobloxVersionsPath { get; }
    public string ModificationsPath { get; }
    public string GlobalSettingsXmlPath { get; }

    private readonly List<FileStream> _openStreams = new();
    private readonly object _lock = new();
    private bool _disposed;

    public MockRobloxEnvironment()
    {
        string id = Guid.NewGuid().ToString("N");
        RootPath = Path.Combine(Path.GetTempPath(), $"BaBaBooeyStrap_Test_{id}");

        BaBaBooeyStrapPath = Path.Combine(RootPath, "BaBaBooeyStrap");
        RobloxPath = Path.Combine(RootPath, "Roblox");
        RobloxLogsPath = Path.Combine(RobloxPath, "logs");
        RobloxVersionsPath = Path.Combine(RobloxPath, "Versions");
        ModificationsPath = Path.Combine(BaBaBooeyStrapPath, "Modifications");
        GlobalSettingsXmlPath = Path.Combine(RobloxPath, "GlobalBasicSettings_13.xml");

        // Create standard directory layout
        Directory.CreateDirectory(BaBaBooeyStrapPath);
        Directory.CreateDirectory(Path.Combine(BaBaBooeyStrapPath, "Logs"));
        Directory.CreateDirectory(Path.Combine(BaBaBooeyStrapPath, "Downloads"));
        Directory.CreateDirectory(Path.Combine(BaBaBooeyStrapPath, "Profiles"));
        Directory.CreateDirectory(ModificationsPath);
        Directory.CreateDirectory(Path.Combine(ModificationsPath, "ClientSettings"));
        Directory.CreateDirectory(Path.Combine(ModificationsPath, "content", "fonts"));
        Directory.CreateDirectory(Path.Combine(ModificationsPath, "content", "sounds"));
        Directory.CreateDirectory(Path.Combine(ModificationsPath, "content", "textures", "Cursors", "KeyboardMouse"));

        Directory.CreateDirectory(RobloxPath);
        Directory.CreateDirectory(RobloxLogsPath);
        Directory.CreateDirectory(RobloxVersionsPath);
    }

    /// <summary>
    /// Creates a mock installed Roblox version folder containing dummy executables and settings.
    /// </summary>
    public string CreateDummyRobloxInstallation(string versionGuid = "version-4ea8e85481e34f46")
    {
        string versionDir = Path.Combine(RobloxVersionsPath, versionGuid);
        Directory.CreateDirectory(versionDir);
        Directory.CreateDirectory(Path.Combine(versionDir, "ClientSettings"));
        Directory.CreateDirectory(Path.Combine(versionDir, "content"));

        File.WriteAllBytes(Path.Combine(versionDir, "RobloxPlayerBeta.exe"), new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        File.WriteAllBytes(Path.Combine(versionDir, "RobloxPlayerLauncher.exe"), new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        File.WriteAllText(Path.Combine(versionDir, "version.txt"), versionGuid);

        return versionDir;
    }

    /// <summary>
    /// Creates a valid GlobalBasicSettings_13.xml file with optional read-only lock.
    /// </summary>
    public void CreateSampleGlobalBasicSettingsXml(int fpsCap = 60, bool readOnly = false)
    {
        var doc = new XDocument(
            new XElement("roblox",
                new XElement("Item", new XAttribute("class", "GlobalSettings"),
                    new XElement("Properties",
                        new XElement("int", new XAttribute("name", "FramerateCap"), fpsCap),
                        new XElement("int", new XAttribute("name", "SavedQualityLevel"), 10),
                        new XElement("float", new XAttribute("name", "MouseSensitivity"), 1.0f)
                    )
                )
            )
        );

        if (File.Exists(GlobalSettingsXmlPath))
        {
            File.SetAttributes(GlobalSettingsXmlPath, FileAttributes.Normal);
        }

        doc.Save(GlobalSettingsXmlPath);

        if (readOnly)
        {
            File.SetAttributes(GlobalSettingsXmlPath, FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// Creates a simulated Roblox log file with real timestamp headers and game join markers.
    /// </summary>
    public string CreateSampleRobloxLogFile(string jobId, long placeId, string serverIp, string? rpcCommand = null)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        string fileName = $"0.640.0.6400599_{timestamp}_Player_abcd_last.log";
        string filePath = Path.Combine(RobloxLogsPath, fileName);

        var sb = new StringBuilder();
        sb.AppendLine($"2026-11-14T14:30:00.000Z,0.000,0,Info [FLog::Output] Log initialized at {timestamp}");
        sb.AppendLine($"2026-11-14T14:30:01.000Z,1.000,0,Info [FLog::Output] ! Joining game '{jobId}' place {placeId} at {serverIp}");
        sb.AppendLine($"2026-11-14T14:30:02.000Z,2.000,0,Info [FLog::Output] UDMUX Address = {serverIp}, Port = 53640 | RCC Server Address = {serverIp}, Port = 53640");
        sb.AppendLine($"2026-11-14T14:30:03.000Z,3.000,0,Info [FLog::Output] universeid:12345678 userid:87654321");
        
        if (!string.IsNullOrEmpty(rpcCommand))
        {
            sb.AppendLine($"2026-11-14T14:30:04.000Z,4.000,0,Info [FLog::Output] [BloxstrapRPC] {rpcCommand}");
        }

        File.WriteAllText(filePath, sb.ToString());
        return filePath;
    }

    /// <summary>
    /// Opens an active streaming log writer with FileShare.ReadWrite to simulate live game output
    /// for testing non-blocking log stream reading in ActivityWatcher.
    /// </summary>
    public SimulatedLogWriter CreateActiveLogStream(string? logFileName = null)
    {
        string fileName = logFileName ?? $"0.640.0.6400599_{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}_Player_live.log";
        string filePath = Path.Combine(RobloxLogsPath, fileName);

        var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        lock (_lock)
        {
            _openStreams.Add(stream);
        }
        return new SimulatedLogWriter(stream, filePath);
    }

    /// <summary>
    /// Creates a zip archive in memory containing specified file contents.
    /// </summary>
    public static byte[] CreateZipBytes(IReadOnlyDictionary<string, string> files)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (fileName, content) in files)
            {
                var entry = archive.CreateEntry(fileName);
                using var entryStream = entry.Open();
                using var writer = new StreamWriter(entryStream, Encoding.UTF8);
                writer.Write(content);
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Creates a zip archive in memory containing specified binary file contents.
    /// </summary>
    public static byte[] CreateZipBytes(IReadOnlyDictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (fileName, content) in files)
            {
                var entry = archive.CreateEntry(fileName);
                using var entryStream = entry.Open();
                entryStream.Write(content, 0, content.Length);
            }
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var stream in _openStreams)
            {
                try { stream.Dispose(); } catch { }
            }
            _openStreams.Clear();

            // Robust directory cleanup with retry
            if (Directory.Exists(RootPath))
            {
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    try
                    {
                        // Remove read-only/hidden/system attributes from files and subdirectories before deleting
                        var dirInfo = new DirectoryInfo(RootPath);
                        foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
                        {
                            try { file.Attributes = FileAttributes.Normal; } catch { }
                        }
                        foreach (var subDir in dirInfo.GetDirectories("*", SearchOption.AllDirectories))
                        {
                            try { subDir.Attributes = FileAttributes.Normal; } catch { }
                        }
                        try { dirInfo.Attributes = FileAttributes.Normal; } catch { }

                        Directory.Delete(RootPath, recursive: true);
                        break;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        Thread.Sleep(50 * attempt);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 5)
                    {
                        Thread.Sleep(50 * attempt);
                    }
                    catch
                    {
                        // Suppress exception during teardown to avoid obscuring test failures
                        break;
                    }
                }
            }
        }
    }
}

public sealed class SimulatedLogWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    public string FilePath { get; }

    public SimulatedLogWriter(FileStream stream, string filePath)
    {
        _stream = stream;
        _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };
        FilePath = filePath;
    }

    public void WriteLine(string line) => _writer.WriteLine(line);
    public void Write(string text) => _writer.Write(text);

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }
}
