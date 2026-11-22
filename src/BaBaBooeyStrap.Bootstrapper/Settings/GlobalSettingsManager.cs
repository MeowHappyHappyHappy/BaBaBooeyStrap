using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Bootstrapper.Settings;

public readonly record struct GlobalSettingsBackup(bool Existed, string? Content);

public interface IGlobalSettingsManager
{
    bool Loaded { get; }
    string FilePath { get; }
    bool ReadOnlyLocked { get; set; }
    int FramerateCap { get; set; }
    float MouseSensitivity { get; set; }
    int SavedQualityLevel { get; set; }
    bool VREnabled { get; set; }
    float PreferredTransparency { get; set; }
    bool ReducedMotion { get; set; }
    int PreferredTextSize { get; set; }

    void Load();
    void Save();
    string? GetValue(string xpath);
    void SetValue(string xpath, object? value);
    void EnsureTemplateExists(int defaultFps = 60);
    string? CaptureBackup();
    void RestoreBackup(string? backupContent);
    GlobalSettingsBackup CaptureBackupState()
    {
        var content = CaptureBackup();
        return new GlobalSettingsBackup(content is not null, content);
    }

    void RestoreBackupState(GlobalSettingsBackup backup)
    {
        if (backup.Existed)
        {
            RestoreBackup(backup.Content);
        }
    }

    void ApplyProfile(RobloxClientSettingsProfile profile);
}

public class GlobalSettingsManager : IGlobalSettingsManager
{
    private const string PropertiesXPath = "//Item/Properties";
    private readonly string _filePath;
    private XDocument? _document;

    public bool Loaded => _document != null;
    public string FilePath => _filePath;

    public GlobalSettingsManager(string? filePath = null)
    {
        _filePath = filePath ?? Paths.RobloxGlobalSettings;
    }

    public bool ReadOnlyLocked
    {
        get => File.Exists(_filePath) && (File.GetAttributes(_filePath) & FileAttributes.ReadOnly) != 0;
        set => SetReadOnlyAttribute(value);
    }

    public int FramerateCap
    {
        get
        {
            string? valStr = GetValue("//int[@name='FramerateCap']");
            return int.TryParse(valStr, out int val) ? val : 60;
        }
        set
        {
            int clamped = Math.Clamp(value, 0, 1000);
            SetValueUnderProperties("int", "FramerateCap", clamped);
        }
    }

    public float MouseSensitivity
    {
        get
        {
            string? valStr = GetValue("//float[@name='MouseSensitivity']");
            return float.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float val) ? val : 1.0f;
        }
        set
        {
            string valStr = value.ToString("0.000000000", CultureInfo.InvariantCulture);
            SetValueUnderProperties("float", "MouseSensitivity", valStr);
            SetVector2Values("MouseSensitivityFirstPerson", valStr, valStr);
            SetVector2Values("MouseSensitivityThirdPerson", valStr, valStr);
        }
    }

    public int SavedQualityLevel
    {
        get
        {
            string? valStr = GetValue("//token[@name='SavedQualityLevel']") ?? GetValue("//int[@name='SavedQualityLevel']");
            return int.TryParse(valStr, out int val) ? val : 1;
        }
        set
        {
            SetValueUnderProperties("token", "SavedQualityLevel", value);
        }
    }

    public bool VREnabled
    {
        get
        {
            string? valStr = GetValue("//bool[@name='VREnabled']");
            return bool.TryParse(valStr, out bool val) && val;
        }
        set
        {
            SetValueUnderProperties("bool", "VREnabled", value.ToString().ToLowerInvariant());
        }
    }

    public float PreferredTransparency
    {
        get
        {
            string? valStr = GetValue("//float[@name='PreferredTransparency']");
            return float.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float val) ? val : 1.0f;
        }
        set
        {
            SetValueUnderProperties("float", "PreferredTransparency", value.ToString(CultureInfo.InvariantCulture));
        }
    }

    public bool ReducedMotion
    {
        get
        {
            string? valStr = GetValue("//bool[@name='ReducedMotion']");
            return bool.TryParse(valStr, out bool val) && val;
        }
        set
        {
            SetValueUnderProperties("bool", "ReducedMotion", value.ToString().ToLowerInvariant());
        }
    }

    public int PreferredTextSize
    {
        get
        {
            string? valStr = GetValue("//token[@name='PreferredTextSize']") ?? GetValue("//int[@name='PreferredTextSize']");
            return int.TryParse(valStr, out int val) ? val : 1;
        }
        set
        {
            SetValueUnderProperties("token", "PreferredTextSize", value);
        }
    }

    public void Load()
    {
        EnsureTemplateExists();
        try
        {
            _document = XDocument.Load(_filePath);
        }
        catch (System.Xml.XmlException)
        {
            string backup = $"{_filePath}.corrupted.{DateTime.UtcNow:yyyyMMdd_HHmmss}.bak";
            try { File.Copy(_filePath, backup, true); } catch { }
            EnsureConfigured(_filePath, 60, overwrite: true);
            _document = XDocument.Load(_filePath);
        }
    }

    public void Save()
    {
        if (_document == null) return;

        if (!File.Exists(_filePath))
        {
            _document.Save(_filePath);
            return;
        }

        FileAttributes originalAttrs = File.GetAttributes(_filePath);
        FileAttributes writableAttrs = originalAttrs & ~(FileAttributes.ReadOnly | FileAttributes.Hidden);
        if (writableAttrs == 0)
        {
            writableAttrs = FileAttributes.Normal;
        }

        try
        {
            if (writableAttrs != originalAttrs)
            {
                File.SetAttributes(_filePath, writableAttrs);
            }

            _document.Save(_filePath);
        }
        finally
        {
            if (File.Exists(_filePath) && originalAttrs != writableAttrs)
            {
                File.SetAttributes(_filePath, originalAttrs);
            }
        }
    }

    public string? GetValue(string xpath)
    {
        return _document?.XPathSelectElement(xpath)?.Value;
    }

    public void SetValue(string xpath, object? value)
    {
        if (_document == null) Load();
        var element = _document?.XPathSelectElement(xpath);
        if (element != null)
        {
            element.Value = value?.ToString() ?? string.Empty;
        }
        else
        {
            var props = GetOrCreatePropertiesElement();
            if (props != null && TryParseXPathForElement(xpath, out string tagName, out string attrName))
            {
                var newElement = new XElement(tagName, new XAttribute("name", attrName), value?.ToString() ?? string.Empty);
                props.Add(newElement);
            }
        }
    }

    private void SetValueUnderProperties(string tagName, string attrName, object? value)
    {
        if (_document == null) Load();
        var props = GetOrCreatePropertiesElement();
        var element = props?
            .Elements()
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), attrName, StringComparison.Ordinal))
            ?? _document?.XPathSelectElement($"//*[@name='{attrName}']");

        string text = value?.ToString() ?? string.Empty;
        if (element != null)
        {
            element.Name = tagName;
            element.Value = text;
            props?.Elements()
                .Where(e => !ReferenceEquals(e, element) &&
                            string.Equals((string?)e.Attribute("name"), attrName, StringComparison.Ordinal))
                .ToList()
                .ForEach(dup => dup.Remove());
        }
        else
        {
            props?.Add(new XElement(tagName, new XAttribute("name", attrName), text));
        }
    }

    private void SetVector2Values(string propertyName, string xValue, string yValue)
    {
        if (_document == null) Load();
        var elem = _document?.XPathSelectElement($"//Vector2[@name='{propertyName}']");
        if (elem == null)
        {
            var props = GetOrCreatePropertiesElement();
            elem = new XElement("Vector2",
                new XAttribute("name", propertyName),
                new XElement("X", xValue),
                new XElement("Y", yValue));
            props?.Add(elem);
        }
        else
        {
            var xElem = elem.Element("X");
            if (xElem == null) elem.Add(new XElement("X", xValue));
            else xElem.Value = xValue;

            var yElem = elem.Element("Y");
            if (yElem == null) elem.Add(new XElement("Y", yValue));
            else yElem.Value = yValue;
        }
    }

    private XElement? GetOrCreatePropertiesElement()
    {
        if (_document == null) return null;
        var props = _document.XPathSelectElement(PropertiesXPath);
        if (props == null)
        {
            var item = _document.Root?.Element("Item");
            if (item == null)
            {
                item = new XElement("Item", new XAttribute("class", "GlobalBasicSettings"));
                _document.Root?.Add(item);
            }
            props = new XElement("Properties");
            item.Add(props);
        }
        return props;
    }

    private void SetReadOnlyAttribute(bool readOnly)
    {
        if (!File.Exists(_filePath)) return;
        var attrs = File.GetAttributes(_filePath);
        if (readOnly) attrs |= FileAttributes.ReadOnly;
        else attrs &= ~FileAttributes.ReadOnly;
        File.SetAttributes(_filePath, attrs);
    }

    public void EnsureTemplateExists(int defaultFps = 60)
    {
        EnsureConfigured(_filePath, defaultFps);
    }

    public string? CaptureBackup()
    {
        return File.Exists(_filePath) ? File.ReadAllText(_filePath) : null;
    }

    public void RestoreBackup(string? backupContent)
    {
        if (backupContent is null)
        {
            return;
        }

        WriteAtomicContent(backupContent);
        RefreshLoadedDocument();
    }

    public GlobalSettingsBackup CaptureBackupState()
    {
        return File.Exists(_filePath)
            ? new GlobalSettingsBackup(true, File.ReadAllText(_filePath))
            : new GlobalSettingsBackup(false, null);
    }

    public void RestoreBackupState(GlobalSettingsBackup backup)
    {
        if (!backup.Existed)
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    FileAttributes attrs = File.GetAttributes(_filePath);
                    if ((attrs & (FileAttributes.ReadOnly | FileAttributes.Hidden)) != 0)
                    {
                        FileAttributes clean = attrs & ~(FileAttributes.ReadOnly | FileAttributes.Hidden);
                        File.SetAttributes(_filePath, clean == 0 ? FileAttributes.Normal : clean);
                    }

                    File.Delete(_filePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            _document = null;
            return;
        }

        if (backup.Content is not null)
        {
            WriteAtomicContent(backup.Content);
            RefreshLoadedDocument();
        }
    }

    public void ApplyProfile(RobloxClientSettingsProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.HasOverrides)
        {
            return;
        }

        var doc = File.Exists(_filePath)
            ? XDocument.Load(_filePath, LoadOptions.PreserveWhitespace)
            : CreateMinimalProfileDocument();

        var propertiesElement = doc.Descendants("Properties").FirstOrDefault();
        if (propertiesElement is null)
        {
            throw new InvalidDataException($"Could not locate <Properties> element in '{_filePath}'.");
        }

        if (profile.MasterVolumePercent.HasValue)
        {
            var clamped = Math.Clamp(profile.MasterVolumePercent.Value, 0, 100);
            var volume = (clamped / 100.0).ToString("0.########", CultureInfo.InvariantCulture);
            SetProfileProperty(propertiesElement, "float", "MasterVolume", volume);
        }

        if (profile.QualityLevel.HasValue)
        {
            var level = Math.Clamp(profile.QualityLevel.Value, 0, 10);
            SetProfileProperty(propertiesElement, "token", "SavedQualityLevel", level.ToString(CultureInfo.InvariantCulture));
            var graphicsLevel = level == 0
                ? 0
                : (int)Math.Round(1 + (level - 1) * 20.0 / 9.0, MidpointRounding.AwayFromZero);
            SetProfileProperty(propertiesElement, "int", "GraphicsQualityLevel", graphicsLevel.ToString(CultureInfo.InvariantCulture));
        }

        if (profile.FramerateCap.HasValue)
        {
            var fps = Math.Max(0, profile.FramerateCap.Value);
            SetProfileProperty(propertiesElement, "int", "FramerateCap", fps.ToString(CultureInfo.InvariantCulture));
        }

        if (profile.Fullscreen.HasValue)
        {
            SetProfileProperty(propertiesElement, "bool", "Fullscreen", profile.Fullscreen.Value ? "true" : "false");
        }

        foreach (var custom in profile.CustomOverrides)
        {
            if (string.IsNullOrWhiteSpace(custom.Key))
            {
                continue;
            }

            SetProfileProperty(propertiesElement, InferElementTag(custom.Value), custom.Key.Trim(), custom.Value.Trim());
        }

        ExecuteWithWritableFile(tempPath =>
        {
            doc.Save(tempPath, SaveOptions.DisableFormatting);
        });

        RefreshLoadedDocument();
    }

    private void WriteAtomicContent(string content)
    {
        ExecuteWithWritableFile(tempPath =>
        {
            File.WriteAllText(tempPath, content);
        });
    }

    private void ExecuteWithWritableFile(Action<string> writeTempFile)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        bool existed = File.Exists(_filePath);
        FileAttributes originalAttrs = existed ? File.GetAttributes(_filePath) : FileAttributes.Normal;
        FileAttributes writableAttrs = originalAttrs & ~(FileAttributes.ReadOnly | FileAttributes.Hidden);
        if (writableAttrs == 0)
        {
            writableAttrs = FileAttributes.Normal;
        }

        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            writeTempFile(tempPath);

            if (existed && writableAttrs != originalAttrs)
            {
                File.SetAttributes(_filePath, writableAttrs);
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch { }

            if (existed && File.Exists(_filePath) && originalAttrs != writableAttrs)
            {
                File.SetAttributes(_filePath, originalAttrs);
            }
        }
    }

    private void RefreshLoadedDocument()
    {
        if (_document is null || !File.Exists(_filePath))
        {
            return;
        }

        try
        {
            _document = XDocument.Load(_filePath);
        }
        catch
        {
            // Keep existing cached document if reload fails transiently
        }
    }

    private static void SetProfileProperty(XElement propertiesElement, string defaultTag, string propertyName, string value)
    {
        var existing = propertiesElement
            .Elements()
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), propertyName, StringComparison.Ordinal));

        if (existing is not null)
        {
            existing.Value = value;
            return;
        }

        propertiesElement.Add(new XElement(defaultTag, new XAttribute("name", propertyName), value));
    }

    private static string InferElementTag(string value)
    {
        var trimmed = value.Trim();
        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
        {
            return "bool";
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return "int";
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return "float";
        }

        return "string";
    }

    private static XDocument CreateMinimalProfileDocument()
    {
        return new XDocument(
            new XElement("roblox",
                new XAttribute("version", "4"),
                new XElement("Item",
                    new XAttribute("class", "UserGameSettings"),
                    new XElement("Properties"))));
    }

    public static void SetFramerateCap(string xmlPath, int fpsCap, bool lockReadOnly = false)
    {
        int clampedFps = Math.Clamp(fpsCap, 0, 1000);

        if (!File.Exists(xmlPath))
        {
            EnsureConfigured(xmlPath, clampedFps);
            if (lockReadOnly)
            {
                File.SetAttributes(xmlPath, File.GetAttributes(xmlPath) | FileAttributes.ReadOnly);
            }
            return;
        }

        FileAttributes originalAttrs = File.GetAttributes(xmlPath);
        FileAttributes writableAttrs = originalAttrs & ~(FileAttributes.ReadOnly | FileAttributes.Hidden);
        if (writableAttrs == 0)
        {
            writableAttrs = FileAttributes.Normal;
        }

        if (writableAttrs != originalAttrs)
        {
            File.SetAttributes(xmlPath, writableAttrs);
        }

        string tempPath = $"{xmlPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            // Check for file locking (FileShare.None)
            using (var testLock = new FileStream(xmlPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            var doc = XDocument.Load(xmlPath);
            var props = doc.Root?.Element("Item")?.Element("Properties");
            if (props == null)
            {
                var item = new XElement("Item",
                    new XAttribute("class", "GlobalBasicSettings"),
                    new XElement("Properties"));
                doc.Root?.Add(item);
                props = item.Element("Properties");
            }

            var capElem = props?.XPathSelectElement("int[@name='FramerateCap']");
            if (capElem == null)
            {
                props?.Add(new XElement("int", new XAttribute("name", "FramerateCap"), clampedFps));
            }
            else
            {
                capElem.Value = clampedFps.ToString();
            }

            doc.Save(tempPath);
            File.Move(tempPath, xmlPath, overwrite: true);
        }
        catch (System.Xml.XmlException)
        {
            string backup = $"{xmlPath}.corrupted.{DateTime.UtcNow:yyyyMMdd_HHmmss}.bak";
            try { File.Copy(xmlPath, backup, true); } catch { }
            EnsureConfigured(xmlPath, clampedFps, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch { }

            if (File.Exists(xmlPath))
            {
                FileAttributes finalAttrs = originalAttrs;
                if (lockReadOnly)
                {
                    finalAttrs |= FileAttributes.ReadOnly;
                }
                if (finalAttrs != writableAttrs)
                {
                    File.SetAttributes(xmlPath, finalAttrs);
                }
            }
        }
    }

    public static void EnsureConfigured(string xmlPath, int defaultFps, bool overwrite = false)
    {
        if (File.Exists(xmlPath) && !overwrite) return;

        string? dir = Path.GetDirectoryName(xmlPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var doc = new XDocument(
            new XElement("roblox",
                new XAttribute("version", "4"),
                new XElement("Item",
                    new XAttribute("class", "GlobalBasicSettings"),
                    new XElement("Properties",
                        new XElement("int", new XAttribute("name", "FramerateCap"), defaultFps),
                        new XElement("float", new XAttribute("name", "MouseSensitivity"), "1.5"),
                        new XElement("token", new XAttribute("name", "SavedQualityLevel"), "10"),
                        new XElement("bool", new XAttribute("name", "VREnabled"), "false"),
                        new XElement("float", new XAttribute("name", "PreferredTransparency"), "1.0"),
                        new XElement("bool", new XAttribute("name", "ReducedMotion"), "false"),
                        new XElement("token", new XAttribute("name", "PreferredTextSize"), "1")
                    )
                )
            )
        );
        doc.Save(xmlPath);
    }

    private static bool TryParseXPathForElement(string xpath, out string tagName, out string attrName)
    {
        tagName = string.Empty;
        attrName = string.Empty;
        var match = Regex.Match(xpath, @"/([a-zA-Z0-9]+)\[@name='([^']+)'\]$");
        if (match.Success)
        {
            tagName = match.Groups[1].Value;
            attrName = match.Groups[2].Value;
            return true;
        }
        return false;
    }
}
