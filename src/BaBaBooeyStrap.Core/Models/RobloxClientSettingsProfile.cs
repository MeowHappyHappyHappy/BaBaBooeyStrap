using System.Text.Json.Serialization;

namespace BaBaBooeyStrap.Core.Models;

public sealed class CustomSettingOverride
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class RobloxClientSettingsProfile
{
    public int? MasterVolumePercent { get; set; }
    public int? QualityLevel { get; set; }
    public int? FramerateCap { get; set; }
    public bool? Fullscreen { get; set; }
    public List<CustomSettingOverride> CustomOverrides { get; set; } = [];

    [JsonIgnore]
    public bool HasOverrides =>
        MasterVolumePercent.HasValue ||
        QualityLevel.HasValue ||
        FramerateCap.HasValue ||
        Fullscreen.HasValue ||
        CustomOverrides.Any(o => !string.IsNullOrWhiteSpace(o.Key));

    public RobloxClientSettingsProfile Clone()
    {
        return new RobloxClientSettingsProfile
        {
            MasterVolumePercent = MasterVolumePercent,
            QualityLevel = QualityLevel,
            FramerateCap = FramerateCap,
            Fullscreen = Fullscreen,
            CustomOverrides = CustomOverrides
                .Select(o => new CustomSettingOverride { Key = o.Key, Value = o.Value })
                .ToList()
        };
    }

    public static RobloxClientSettingsProfile CreateAfkLowPerfPreset() => new()
    {
        MasterVolumePercent = 0,
        QualityLevel = 1,
        FramerateCap = 30,
        Fullscreen = false
    };

    public string ToSummaryString() =>
        ToSummaryString(
            defaultLabel: "預設",
            volumeFormat: "音量 {0}%",
            qualityAutoLabel: "畫質 自動",
            qualityLevelFormat: "畫質 {0}",
            fpsFormat: "FPS {0}",
            fullscreenLabel: "全螢幕",
            windowedLabel: "視窗",
            customCountFormat: "自訂×{0}");

    public string ToSummaryString(
        string defaultLabel,
        string volumeFormat,
        string qualityAutoLabel,
        string qualityLevelFormat,
        string fpsFormat,
        string fullscreenLabel,
        string windowedLabel,
        string customCountFormat)
    {
        if (!HasOverrides)
        {
            return defaultLabel;
        }

        var parts = new List<string>();
        if (MasterVolumePercent.HasValue)
        {
            parts.Add(string.Format(volumeFormat, MasterVolumePercent.Value));
        }

        if (QualityLevel.HasValue)
        {
            parts.Add(QualityLevel.Value == 0
                ? qualityAutoLabel
                : string.Format(qualityLevelFormat, QualityLevel.Value));
        }

        if (FramerateCap.HasValue)
        {
            parts.Add(string.Format(fpsFormat, FramerateCap.Value));
        }

        if (Fullscreen.HasValue)
        {
            parts.Add(Fullscreen.Value ? fullscreenLabel : windowedLabel);
        }

        var customCount = CustomOverrides.Count(o => !string.IsNullOrWhiteSpace(o.Key));
        if (customCount > 0)
        {
            parts.Add(string.Format(customCountFormat, customCount));
        }

        return string.Join(" / ", parts);
    }
}
