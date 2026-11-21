using System.Security;
using System.Text.RegularExpressions;
using ICSharpCode.SharpZipLib.Zip;
using BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;

namespace BaBaBooeyStrap.Bootstrapper.Deployment;

public class PackageExtractor : IPackageExtractor
{
    public static readonly IReadOnlyDictionary<string, string> PackageDirectoryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "RobloxApp.zip", "" },
        { "redist.zip", "" },
        { "shaders.zip", "shaders\\" },
        { "ssl.zip", "ssl\\" },
        { "WebView2.zip", "" },
        { "WebView2RuntimeInstaller.zip", "WebView2RuntimeInstaller\\" },
        { "content-avatar.zip", "content\\avatar\\" },
        { "content-configs.zip", "content\\configs\\" },
        { "content-fonts.zip", "content\\fonts\\" },
        { "content-sky.zip", "content\\sky\\" },
        { "content-sounds.zip", "content\\sounds\\" },
        { "content-textures2.zip", "content\\textures\\" },
        { "content-models.zip", "content\\models\\" },
        { "content-textures3.zip", "PlatformContent\\pc\\textures\\" },
        { "content-terrain.zip", "PlatformContent\\pc\\terrain\\" },
        { "content-platform-fonts.zip", "PlatformContent\\pc\\fonts\\" },
        { "content-platform-dictionaries.zip", "PlatformContent\\pc\\shared_compression_dictionaries\\" },
        { "extracontent-luapackages.zip", "ExtraContent\\LuaPackages\\" },
        { "extracontent-translations.zip", "ExtraContent\\translations\\" },
        { "extracontent-models.zip", "ExtraContent\\models\\" },
        { "extracontent-textures.zip", "ExtraContent\\textures\\" },
        { "extracontent-places.zip", "ExtraContent\\places\\" },
        { "RobloxStudio.zip", "" },
        { "ApplicationConfig.zip", "ApplicationConfig\\" },
        { "BuiltInPlugins.zip", "BuiltInPlugins\\" },
        { "BuiltInStandalonePlugins.zip", "BuiltInStandalonePlugins\\" },
        { "content-qt_translations.zip", "content\\qt_translations\\" },
        { "content-api-docs.zip", "content\\api_docs\\" },
        { "extracontent-scripts.zip", "ExtraContent\\scripts\\" },
        { "Libraries.zip", "" },
        { "LibrariesQt5.zip", "" },
        { "Plugins.zip", "Plugins\\" },
        { "Qml.zip", "Qml\\" },
        { "StudioFonts.zip", "StudioFonts\\" }
    };

    public void ExtractZip(byte[] zipBytes, string targetDirectory)
    {
        ArgumentNullException.ThrowIfNull(zipBytes);
        ArgumentNullException.ThrowIfNull(targetDirectory);

        if (zipBytes.Length == 0)
        {
            throw new InvalidDataException("Zero-byte zip archive is invalid.");
        }

        using var memoryStream = new MemoryStream(zipBytes);
        ExtractFromStream(memoryStream, targetDirectory);
    }

    public void ExtractZipFile(string zipFilePath, string targetDirectory, string? fileFilter = null)
    {
        ArgumentNullException.ThrowIfNull(zipFilePath);
        ArgumentNullException.ThrowIfNull(targetDirectory);

        if (!File.Exists(zipFilePath))
        {
            throw new FileNotFoundException("Zip package file not found.", zipFilePath);
        }

        var fileInfo = new FileInfo(zipFilePath);
        if (fileInfo.Length == 0)
        {
            throw new InvalidDataException("Zero-byte zip file is invalid.");
        }

        using var stream = File.OpenRead(zipFilePath);
        ExtractFromStream(stream, targetDirectory, fileFilter);
    }

    public async Task ExtractPackageAsync(
        string packageZipPath,
        string targetVersionDirectory,
        string relativeSubdirectory,
        IEnumerable<string>? specificFiles = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        string fullTargetDir = Path.Combine(targetVersionDirectory, relativeSubdirectory ?? string.Empty);
        Directory.CreateDirectory(fullTargetDir);

        if (specificFiles != null && specificFiles.Any())
        {
            string pattern = "^(" + string.Join("|", specificFiles.Select(Regex.Escape)) + ")$";
            ExtractZipFile(packageZipPath, fullTargetDir, pattern);
        }
        else
        {
            ExtractZipFile(packageZipPath, fullTargetDir);
        }

        await Task.CompletedTask;
    }

    private static void ExtractFromStream(Stream stream, string targetDirectory, string? fileFilter = null)
    {
        Directory.CreateDirectory(targetDirectory);
        string fullTargetDir = Path.GetFullPath(targetDirectory);
        if (!fullTargetDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            fullTargetDir += Path.DirectorySeparatorChar;
        }

        ZipFile? zipFile = null;
        try
        {
            zipFile = new ZipFile(stream) { IsStreamOwner = false };
        }
        catch (Exception ex) when (ex is not SecurityException)
        {
            throw new InvalidDataException("Failed to open zip archive. Archive may be corrupted or malformed.", ex);
        }

        Regex? filterRegex = string.IsNullOrEmpty(fileFilter) ? null : new Regex(fileFilter, RegexOptions.IgnoreCase);

        try
        {
            foreach (ZipEntry entry in zipFile)
            {
                if (filterRegex != null && !filterRegex.IsMatch(entry.Name))
                {
                    continue;
                }

                bool isDirectory = entry.IsDirectory || entry.Name.EndsWith('/') || entry.Name.EndsWith('\\');
                string normalizedEntryName = entry.Name
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .TrimStart(Path.DirectorySeparatorChar);

                if (string.IsNullOrWhiteSpace(normalizedEntryName))
                {
                    continue;
                }

                // ZipSlip directory traversal check
                string destinationPath = Path.GetFullPath(Path.Combine(fullTargetDir, normalizedEntryName));
                if (!destinationPath.StartsWith(fullTargetDir, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SecurityException($"ZipSlip directory traversal detected in entry: {entry.Name}");
                }

                if (isDirectory)
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                string? parentDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                try
                {
                    if (File.Exists(destinationPath))
                    {
                        var attrs = File.GetAttributes(destinationPath);
                        if (attrs.HasFlag(FileAttributes.ReadOnly))
                        {
                            File.SetAttributes(destinationPath, attrs & ~FileAttributes.ReadOnly);
                        }
                    }

                    using var entryStream = zipFile.GetInputStream(entry);
                    using var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    entryStream.CopyTo(destStream);
                }
                catch (IOException)
                {
                    // DirectWrite / GDI Windows font cache locks font files (.ttf, .otf, .ttc)
                    if (destinationPath.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                        destinationPath.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
                        destinationPath.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
                    {
                        // Tolerated font lock - continue extraction
                        continue;
                    }
                    throw;
                }
            }
        }
        finally
        {
            zipFile?.Close();
        }
    }
}
