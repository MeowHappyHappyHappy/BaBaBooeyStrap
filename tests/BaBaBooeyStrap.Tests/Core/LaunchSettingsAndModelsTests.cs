using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.Tests.Core;

public class LaunchSettingsAndModelsTests
{
    [Fact]
    public void LaunchSettings_ParseArgs_MenuFlag()
    {
        var settings1 = new LaunchSettings(["-menu"]);
        settings1.MenuFlag.Active.Should().BeTrue();

        var settings2 = new LaunchSettings(["-preferences"]);
        settings2.MenuFlag.Active.Should().BeTrue();

        var settings3 = new LaunchSettings(["-settings"]);
        settings3.MenuFlag.Active.Should().BeTrue();
    }

    [Fact]
    public void LaunchSettings_ParseArgs_PlayerUri()
    {
        string uri = "roblox-player:1+launchmode:play+gameinfo:test";
        var settings = new LaunchSettings([uri, "-quiet"]);

        settings.RobloxLaunchMode.Should().Be(LaunchMode.Player);
        settings.RobloxLaunchArgs.Should().Be(uri);
        settings.QuietFlag.Active.Should().BeTrue();
    }

    [Fact]
    public void LaunchSettings_ParseArgs_PlayerAndStudioFlags()
    {
        var playerSettings = new LaunchSettings(["-player", "roblox-player:test"]);
        playerSettings.RobloxLaunchMode.Should().Be(LaunchMode.Player);
        playerSettings.RobloxLaunchArgs.Should().Be("roblox-player:test");

        var studioSettings = new LaunchSettings(["-studio", "C:\\Place.rbxl"]);
        studioSettings.RobloxLaunchMode.Should().Be(LaunchMode.Studio);
        studioSettings.RobloxLaunchArgs.Should().Contain("-task EditFile -localPlaceFile \"C:\\Place.rbxl\"");
    }

    [Fact]
    public void FastFlag_InferType_InfersCorrectly()
    {
        FastFlag.InferType("FFlagEnableNewUI").Should().Be(FastFlagType.Boolean);
        FastFlag.InferType("DFFlagDisableDPIScale").Should().Be(FastFlagType.Boolean);
        FastFlag.InferType("SFFlagSecurityCheck").Should().Be(FastFlagType.Boolean);

        FastFlag.InferType("FIntTargetFps").Should().Be(FastFlagType.Integer);
        FastFlag.InferType("DFIntMaxMemory").Should().Be(FastFlagType.Integer);
        FastFlag.InferType("SFIntNetworkTimeout").Should().Be(FastFlagType.Integer);

        FastFlag.InferType("FStringCustomEndpoint").Should().Be(FastFlagType.String);
        FastFlag.InferType("DFStringChannelOverride").Should().Be(FastFlagType.String);

        FastFlag.InferType("FFLogGraphicsEngine").Should().Be(FastFlagType.Log);

        FastFlag.InferType("CustomRandomFlag").Should().Be(FastFlagType.Unknown);
        FastFlag.InferType("").Should().Be(FastFlagType.Unknown);
    }

    [Fact]
    public void RobloxClientSettingsProfile_CloneAndHasOverrides()
    {
        var emptyProfile = new RobloxClientSettingsProfile();
        emptyProfile.HasOverrides.Should().BeFalse();

        var profile = new RobloxClientSettingsProfile
        {
            FramerateCap = 165,
            QualityLevel = 8,
            MasterVolumePercent = 50,
            Fullscreen = true,
            CustomOverrides = [new CustomSettingOverride { Key = "TestKey", Value = "TestVal" }]
        };

        profile.HasOverrides.Should().BeTrue();

        var clone = profile.Clone();
        clone.FramerateCap.Should().Be(165);
        clone.QualityLevel.Should().Be(8);
        clone.MasterVolumePercent.Should().Be(50);
        clone.Fullscreen.Should().BeTrue();
        clone.CustomOverrides.Should().HaveCount(1);
        clone.CustomOverrides[0].Key.Should().Be("TestKey");
        clone.CustomOverrides[0].Value.Should().Be("TestVal");

        // Verify deep copy of list
        clone.CustomOverrides.Add(new CustomSettingOverride { Key = "SecondKey", Value = "SecondVal" });
        profile.CustomOverrides.Should().HaveCount(1);
    }
}
