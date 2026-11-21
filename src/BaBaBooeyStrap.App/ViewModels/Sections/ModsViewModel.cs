using System.IO;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Bootstrapper.Mods.Presets;
using BaBaBooeyStrap.Core;

namespace BaBaBooeyStrap.App.ViewModels.Sections;

public sealed class ModsViewModel : ViewModelBase
{
    private readonly IPaths _paths;
    private readonly Action<string, object?[]> _setLocalizedStatus;

    private string _currentFontStatus = LocalizationService.Instance.GetString("Str_Mods_DefaultFontStatus");
    private string _fontStatusKey = "Str_Mods_DefaultFontStatus";
    private object?[] _fontStatusArgs = Array.Empty<object?>();
    private CursorPreset _selectedCursorPreset = CursorPreset.Default;
    private bool _hasCustomDeathSound;
    private bool _oldAvatarBackgroundEnabled;
    private int _activeModFileCount;

    public ModsViewModel(IPaths paths, Action<string, object?[]> setLocalizedStatus)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _setLocalizedStatus = setLocalizedStatus ?? throw new ArgumentNullException(nameof(setLocalizedStatus));

        RefreshModStatus();
    }

    public string CurrentFontStatus
    {
        get => _currentFontStatus;
        private set => SetProperty(ref _currentFontStatus, value);
    }

    public CursorPreset SelectedCursorPreset
    {
        get => _selectedCursorPreset;
        set
        {
            if (SetProperty(ref _selectedCursorPreset, value))
            {
                ApplyCursorPreset(value);
            }
        }
    }

    public bool HasCustomDeathSound
    {
        get => _hasCustomDeathSound;
        private set
        {
            if (SetProperty(ref _hasCustomDeathSound, value))
            {
                OnPropertyChanged(nameof(CustomDeathSoundStatusText));
            }
        }
    }

    public string CustomDeathSoundStatusText =>
        LocalizationService.Instance.Format(
            "Str_Mods_DeathSoundActiveFormat",
            LocalizationService.Instance.GetString(HasCustomDeathSound ? "Str_Common_Yes" : "Str_Common_No"));

    public bool OldAvatarBackgroundEnabled
    {
        get => _oldAvatarBackgroundEnabled;
        set
        {
            if (SetProperty(ref _oldAvatarBackgroundEnabled, value))
            {
                AvatarBackgroundPresetHandler.SetOldAvatarBackground(value, _paths.Modifications);
                RefreshModStatus();
            }
        }
    }

    public int ActiveModFileCount
    {
        get => _activeModFileCount;
        private set => SetProperty(ref _activeModFileCount, value);
    }

    public bool ApplyCustomFont(string filePath)
    {
        var validator = new FontValidator();
        if (!validator.ValidateFont(filePath, out _, out var error))
        {
            _fontStatusKey = "Str_Mods_InvalidFontFormat";
            _fontStatusArgs = [error];
            CurrentFontStatus = LocalizedFormatHelper.Format(_fontStatusKey, _fontStatusArgs);
            _setLocalizedStatus(_fontStatusKey, _fontStatusArgs);
            return false;
        }

        string dest = Path.Combine(_paths.Modifications, @"content\fonts\CustomFont.ttf");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(filePath, dest, overwrite: true);
        RefreshModStatus();
        _setLocalizedStatus("Str_Mods_StatusAppliedFont", Array.Empty<object?>());
        return true;
    }

    public void ResetCustomFont()
    {
        string dest = Path.Combine(_paths.Modifications, @"content\fonts\CustomFont.ttf");
        if (File.Exists(dest)) File.Delete(dest);
        RefreshModStatus();
        _setLocalizedStatus("Str_Mods_StatusResetFont", Array.Empty<object?>());
    }

    public void ApplyCursorPreset(CursorPreset preset)
    {
        CursorPresetHandler.ApplyCursorPreset(_paths.Modifications, preset);
        RefreshModStatus();
        _setLocalizedStatus("Str_Mods_StatusAppliedCursorFormat", [preset]);
    }

    public bool ApplyCustomDeathSound(string filePath)
    {
        try
        {
            DeathSoundPresetHandler.ApplyDeathSoundFile(filePath, _paths.Modifications);
            RefreshModStatus();
            _setLocalizedStatus("Str_Mods_StatusAppliedSound", Array.Empty<object?>());
            return true;
        }
        catch (Exception ex)
        {
            _setLocalizedStatus("Str_Mods_InvalidAudioFormat", [ex.Message]);
            return false;
        }
    }

    public void ResetDeathSound()
    {
        DeathSoundPresetHandler.ResetDeathSound(_paths.Modifications);
        RefreshModStatus();
        _setLocalizedStatus("Str_Mods_StatusResetSound", Array.Empty<object?>());
    }

    public void RefreshModStatus()
    {
        string fontPath = Path.Combine(_paths.Modifications, @"content\fonts\CustomFont.ttf");
        _fontStatusKey = File.Exists(fontPath) ? "Str_Mods_CustomFontActive" : "Str_Mods_DefaultFontStatus";
        _fontStatusArgs = Array.Empty<object?>();
        CurrentFontStatus = LocalizedFormatHelper.Format(_fontStatusKey, _fontStatusArgs);

        string ouchPath = Path.Combine(_paths.Modifications, DeathSoundPresetHandler.RelativeOuchPath);
        HasCustomDeathSound = File.Exists(ouchPath);
        OnPropertyChanged(nameof(CustomDeathSoundStatusText));

        string mobileRbxl = Path.Combine(_paths.Modifications, AvatarBackgroundPresetHandler.RelativeMobileRbxlPath);
        _oldAvatarBackgroundEnabled = File.Exists(mobileRbxl);
        OnPropertyChanged(nameof(OldAvatarBackgroundEnabled));

        ActiveModFileCount = Directory.Exists(_paths.Modifications)
            ? Directory.GetFiles(_paths.Modifications, "*", SearchOption.AllDirectories).Length
            : 0;
    }

    public void RefreshLocalizedProperties()
    {
        CurrentFontStatus = LocalizedFormatHelper.Format(_fontStatusKey, _fontStatusArgs);
        OnPropertyChanged(nameof(CustomDeathSoundStatusText));
    }
}
