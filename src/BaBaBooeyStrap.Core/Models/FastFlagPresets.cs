namespace BaBaBooeyStrap.Core.Models;

public static class FastFlagPresets
{
    public static readonly IReadOnlyDictionary<string, string> PresetFlags = new Dictionary<string, string>
    {
        { "Rendering.ManualFullscreen", "FFlagHandleAltEnterFullscreenManually" },
        { "Rendering.DisableScaling", "DFFlagDisableDPIScale" },
        { "Rendering.MSAA", "FIntDebugForceMSAASamples" },
        { "Rendering.FRMQualityOverride", "DFIntDebugFRMQualityLevelOverride" },
        { "Rendering.Mode.D3D11", "FFlagDebugGraphicsPreferD3D11" },
        { "Rendering.Mode.Vulkan", "FFlagDebugGraphicsPreferVulkan" },
        { "Geometry.MeshLOD.Static", "DFIntCSGLevelOfDetailSwitchingDistanceStatic" },
        { "Geometry.MeshLOD.L0", "DFIntCSGLevelOfDetailSwitchingDistance" },
        { "Geometry.MeshLOD.L12", "DFIntCSGLevelOfDetailSwitchingDistanceL12" },
        { "Geometry.MeshLOD.L23", "DFIntCSGLevelOfDetailSwitchingDistanceL23" },
        { "Geometry.MeshLOD.L34", "DFIntCSGLevelOfDetailSwitchingDistanceL34" }
    };
}
