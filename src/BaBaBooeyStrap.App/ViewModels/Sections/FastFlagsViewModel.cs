using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.App.ViewModels.Sections;

public sealed class FastFlagsViewModel : ViewModelBase
{
    private readonly Settings _settings;
    private readonly FastFlagManager _fastFlagManager;

    private RenderingMode _selectedRenderingMode = RenderingMode.Default;
    private MSAAMode _selectedMSAAMode = MSAAMode.Default;
    private int _frmQualityOverride = 1;
    private int _meshLodLevel = 3;
    private bool _disableDpiScaling;
    private bool _manualFullscreen = true;

    public FastFlagsViewModel(Settings settings, FastFlagManager fastFlagManager)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fastFlagManager = fastFlagManager ?? throw new ArgumentNullException(nameof(fastFlagManager));

        LoadFastFlagPresets();
    }

    public bool UseFastFlagManager
    {
        get => _settings.UseFastFlagManager;
        set
        {
            if (_settings.UseFastFlagManager != value)
            {
                _settings.UseFastFlagManager = value;
                OnPropertyChanged();
            }
        }
    }

    public bool UseDisableAppPatch
    {
        get => _settings.UseDisableAppPatch;
        set
        {
            if (_settings.UseDisableAppPatch != value)
            {
                _settings.UseDisableAppPatch = value;
                OnPropertyChanged();
            }
        }
    }

    public RenderingMode SelectedRenderingMode
    {
        get => _selectedRenderingMode;
        set => SetProperty(ref _selectedRenderingMode, value);
    }

    public MSAAMode SelectedMSAAMode
    {
        get => _selectedMSAAMode;
        set => SetProperty(ref _selectedMSAAMode, value);
    }

    public int FrmQualityOverride
    {
        get => _frmQualityOverride;
        set => SetProperty(ref _frmQualityOverride, Math.Clamp(value, 1, 21));
    }

    public int MeshLodLevel
    {
        get => _meshLodLevel;
        set => SetProperty(ref _meshLodLevel, Math.Clamp(value, 0, 3));
    }

    public bool DisableDpiScaling
    {
        get => _disableDpiScaling;
        set => SetProperty(ref _disableDpiScaling, value);
    }

    public bool ManualFullscreen
    {
        get => _manualFullscreen;
        set => SetProperty(ref _manualFullscreen, value);
    }

    public FastFlagEditorViewModel CreateFastFlagEditorViewModel() => new(_fastFlagManager);

    public void LoadFastFlagPresets()
    {
        _fastFlagManager.Load();
        if (_fastFlagManager.GetFlag("FFlagDebugGraphicsPreferVulkan")?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true)
            SelectedRenderingMode = RenderingMode.Vulkan;
        else if (_fastFlagManager.GetFlag("FFlagDebugGraphicsPreferD3D11")?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true)
            SelectedRenderingMode = RenderingMode.D3D11;
        else
            SelectedRenderingMode = RenderingMode.Default;

        if (int.TryParse(_fastFlagManager.GetFlag("FIntDebugForceMSAASamples")?.ToString(), out int msaa))
        {
            SelectedMSAAMode = msaa switch
            {
                1 => MSAAMode.x1,
                2 => MSAAMode.x2,
                4 => MSAAMode.x4,
                _ => MSAAMode.Default
            };
        }

        if (int.TryParse(_fastFlagManager.GetFlag("DFIntDebugFRMQualityLevelOverride")?.ToString(), out int frm))
            FrmQualityOverride = frm;

        if (int.TryParse(_fastFlagManager.GetFlag("DFIntCSGLevelOfDetailSwitchingDistanceStatic")?.ToString(), out int lod))
            MeshLodLevel = lod;

        DisableDpiScaling = _fastFlagManager.GetFlag("DFFlagDisableDPIScale")?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true;
        ManualFullscreen = _fastFlagManager.GetFlag("FFlagHandleAltEnterFullscreenManually")?.ToString()?.Equals("False", StringComparison.OrdinalIgnoreCase) == true;
    }

    public void ApplyFastFlagPresets()
    {
        if (!_settings.UseFastFlagManager) return;

        switch (_selectedRenderingMode)
        {
            case RenderingMode.Vulkan:
                _fastFlagManager.ApplyPreset("Rendering.Mode.Vulkan", "True");
                break;
            case RenderingMode.D3D11:
                _fastFlagManager.ApplyPreset("Rendering.Mode.D3D11", "True");
                break;
            default:
                _fastFlagManager.RemoveFlag("FFlagDebugGraphicsPreferVulkan");
                _fastFlagManager.RemoveFlag("FFlagDebugGraphicsPreferD3D11");
                break;
        }

        switch (_selectedMSAAMode)
        {
            case MSAAMode.x1: _fastFlagManager.ApplyPreset("Rendering.MSAA", 1); break;
            case MSAAMode.x2: _fastFlagManager.ApplyPreset("Rendering.MSAA", 2); break;
            case MSAAMode.x4: _fastFlagManager.ApplyPreset("Rendering.MSAA", 4); break;
            default: _fastFlagManager.RemoveFlag("FIntDebugForceMSAASamples"); break;
        }

        _fastFlagManager.ApplyPreset("Rendering.FRMQualityOverride", _frmQualityOverride);
        _fastFlagManager.ApplyPreset("Geometry.MeshLOD", _meshLodLevel);
        _fastFlagManager.ApplyPreset("Rendering.DisableScaling", _disableDpiScaling ? "True" : "False");
        _fastFlagManager.ApplyPreset("Rendering.ManualFullscreen", _manualFullscreen ? "False" : "True");
        _fastFlagManager.Save();
    }
}
