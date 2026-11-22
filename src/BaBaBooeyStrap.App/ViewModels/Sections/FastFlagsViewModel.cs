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

    private bool _writeRenderingMode;
    private bool _writeMSAAMode;
    private bool _writeFrmQualityOverride;
    private bool _writeMeshLodLevel;
    private bool _writeDisableDpiScaling;
    private bool _writeManualFullscreen;

    internal bool IsLoadingPresets { get; private set; }

    public FastFlagsViewModel(Settings settings, FastFlagManager fastFlagManager)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fastFlagManager = fastFlagManager ?? throw new ArgumentNullException(nameof(fastFlagManager));

        LoadFastFlagPresets();
    }

    public FastFlagManager FastFlagManager => _fastFlagManager;

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

    public bool WriteRenderingMode
    {
        get => _writeRenderingMode;
        set => SetProperty(ref _writeRenderingMode, value);
    }

    public bool WriteMSAAMode
    {
        get => _writeMSAAMode;
        set => SetProperty(ref _writeMSAAMode, value);
    }

    public bool WriteFrmQualityOverride
    {
        get => _writeFrmQualityOverride;
        set => SetProperty(ref _writeFrmQualityOverride, value);
    }

    public bool WriteMeshLodLevel
    {
        get => _writeMeshLodLevel;
        set => SetProperty(ref _writeMeshLodLevel, value);
    }

    public bool WriteDisableDpiScaling
    {
        get => _writeDisableDpiScaling;
        set => SetProperty(ref _writeDisableDpiScaling, value);
    }

    public bool WriteManualFullscreen
    {
        get => _writeManualFullscreen;
        set => SetProperty(ref _writeManualFullscreen, value);
    }

    public FastFlagEditorViewModel CreateFastFlagEditorViewModel() => new(_fastFlagManager);

    public void LoadFastFlagPresets()
    {
        IsLoadingPresets = true;
        try
        {
            _fastFlagManager.Load();
            var vulkanFlag = _fastFlagManager.GetFlag("FFlagDebugGraphicsPreferVulkan");
            var d3d11Flag = _fastFlagManager.GetFlag("FFlagDebugGraphicsPreferD3D11");
            WriteRenderingMode = vulkanFlag != null || d3d11Flag != null;
            if (vulkanFlag?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true)
                SelectedRenderingMode = RenderingMode.Vulkan;
            else if (d3d11Flag?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true)
                SelectedRenderingMode = RenderingMode.D3D11;
            else
                SelectedRenderingMode = RenderingMode.Default;

            var msaaFlag = _fastFlagManager.GetFlag("FIntDebugForceMSAASamples");
            WriteMSAAMode = msaaFlag != null;
            if (int.TryParse(msaaFlag?.ToString(), out int msaa))
            {
                SelectedMSAAMode = msaa switch
                {
                    1 => MSAAMode.x1,
                    2 => MSAAMode.x2,
                    4 => MSAAMode.x4,
                    _ => MSAAMode.Default
                };
            }
            else
            {
                SelectedMSAAMode = MSAAMode.Default;
            }

            var frmFlag = _fastFlagManager.GetFlag("DFIntDebugFRMQualityLevelOverride");
            WriteFrmQualityOverride = frmFlag != null;
            if (int.TryParse(frmFlag?.ToString(), out int frm))
                FrmQualityOverride = frm;

            var lodFlag = _fastFlagManager.GetFlag("DFIntCSGLevelOfDetailSwitchingDistanceStatic");
            WriteMeshLodLevel = lodFlag != null || _fastFlagManager.GetFlag("DFIntCSGLevelOfDetailSwitchingDistance") != null;
            if (int.TryParse(lodFlag?.ToString(), out int lod))
                MeshLodLevel = lod;

            var dpiFlag = _fastFlagManager.GetFlag("DFFlagDisableDPIScale");
            WriteDisableDpiScaling = dpiFlag != null;
            DisableDpiScaling = dpiFlag?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true;

            var fullscreenFlag = _fastFlagManager.GetFlag("FFlagHandleAltEnterFullscreenManually");
            WriteManualFullscreen = fullscreenFlag != null;
            ManualFullscreen = fullscreenFlag?.ToString()?.Equals("False", StringComparison.OrdinalIgnoreCase) == true;
        }
        finally
        {
            IsLoadingPresets = false;
        }
    }

    public void ApplyFastFlagPresets()
    {
        if (!_settings.UseFastFlagManager) return;

        if (WriteRenderingMode)
        {
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
        }
        else
        {
            _fastFlagManager.RemoveFlag("FFlagDebugGraphicsPreferVulkan");
            _fastFlagManager.RemoveFlag("FFlagDebugGraphicsPreferD3D11");
        }

        if (WriteMSAAMode)
        {
            switch (_selectedMSAAMode)
            {
                case MSAAMode.x1: _fastFlagManager.ApplyPreset("Rendering.MSAA", 1); break;
                case MSAAMode.x2: _fastFlagManager.ApplyPreset("Rendering.MSAA", 2); break;
                case MSAAMode.x4: _fastFlagManager.ApplyPreset("Rendering.MSAA", 4); break;
                default: _fastFlagManager.RemoveFlag("FIntDebugForceMSAASamples"); break;
            }
        }
        else
        {
            _fastFlagManager.RemoveFlag("FIntDebugForceMSAASamples");
        }

        if (WriteFrmQualityOverride)
            _fastFlagManager.ApplyPreset("Rendering.FRMQualityOverride", _frmQualityOverride);
        else
            _fastFlagManager.RemoveFlag("DFIntDebugFRMQualityLevelOverride");

        if (WriteMeshLodLevel)
        {
            _fastFlagManager.ApplyPreset("Geometry.MeshLOD", _meshLodLevel);
        }
        else
        {
            _fastFlagManager.RemoveFlag("DFIntCSGLevelOfDetailSwitchingDistanceStatic");
            _fastFlagManager.RemoveFlag("DFIntCSGLevelOfDetailSwitchingDistance");
            _fastFlagManager.RemoveFlag("DFIntCSGLevelOfDetailSwitchingDistanceL12");
            _fastFlagManager.RemoveFlag("DFIntCSGLevelOfDetailSwitchingDistanceL23");
            _fastFlagManager.RemoveFlag("DFIntCSGLevelOfDetailSwitchingDistanceL34");
        }

        if (WriteDisableDpiScaling)
            _fastFlagManager.ApplyPreset("Rendering.DisableScaling", _disableDpiScaling ? "True" : "False");
        else
            _fastFlagManager.RemoveFlag("DFFlagDisableDPIScale");

        if (WriteManualFullscreen)
            _fastFlagManager.ApplyPreset("Rendering.ManualFullscreen", _manualFullscreen ? "False" : "True");
        else
            _fastFlagManager.RemoveFlag("FFlagHandleAltEnterFullscreenManually");

        _fastFlagManager.Save();
    }

    public void ResetAllFastFlags()
    {
        _fastFlagManager.Clear();
        _fastFlagManager.Save();
        LoadFastFlagPresets();
    }

    public void RefreshFromSettings()
    {
        LoadFastFlagPresets();
        OnPropertyChanged(nameof(UseFastFlagManager));
        OnPropertyChanged(nameof(UseDisableAppPatch));
    }
}
