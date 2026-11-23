using System.IO;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Bootstrapper.Mods;
using BaBaBooeyStrap.Bootstrapper.Mods.Presets;
using BaBaBooeyStrap.Core;
using BaBaBooeyStrap.Core.Logging;

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
                try
                {
                    AvatarBackgroundPresetHandler.SetOldAvatarBackground(value, _paths.Modifications);
                }
                catch (Exception ex)
                {
                    Logger.ExceptionStatic("ModsViewModel.OldAvatarBackgroundEnabled", ex);
                }
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
        try
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
        catch (Exception ex)
        {
            Logger.ExceptionStatic("ModsViewModel.ApplyCustomFont", ex);
            _fontStatusKey = "Str_Mods_InvalidFontFormat";
            _fontStatusArgs = [ex.Message];
            CurrentFontStatus = LocalizedFormatHelper.Format(_fontStatusKey, _fontStatusArgs);
            _setLocalizedStatus(_fontStatusKey, _fontStatusArgs);
            return false;
        }
    }

    public void ResetCustomFont()
    {
        try
        {
            string dest = Path.Combine(_paths.Modifications, @"content\fonts\CustomFont.ttf");
            if (File.Exists(dest)) File.Delete(dest);
            RefreshModStatus();
            _setLocalizedStatus("Str_Mods_StatusResetFont", Array.Empty<object?>());
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("ModsViewModel.ResetCustomFont", ex);
            _setLocalizedStatus("Str_Mods_InvalidFontFormat", [ex.Message]);
        }
    }

    public void ApplyCursorPreset(CursorPreset preset)
    {
        try
        {
            CursorPresetHandler.ApplyCursorPreset(_paths.Modifications, preset);
            RefreshModStatus();
            _setLocalizedStatus("Str_Mods_StatusAppliedCursorFormat", [preset]);
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("ModsViewModel.ApplyCursorPreset", ex);
            _setLocalizedStatus("Str_Mods_InvalidAudioFormat", [ex.Message]);
        }
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
            Logger.ExceptionStatic("ModsViewModel.ApplyCustomDeathSound", ex);
            _setLocalizedStatus("Str_Mods_InvalidAudioFormat", [ex.Message]);
            return false;
        }
    }

    public void ResetDeathSound()
    {
        try
        {
            DeathSoundPresetHandler.ResetDeathSound(_paths.Modifications);
            RefreshModStatus();
            _setLocalizedStatus("Str_Mods_StatusResetSound", Array.Empty<object?>());
        }
        catch (Exception ex)
        {
            Logger.ExceptionStatic("ModsViewModel.ResetDeathSound", ex);
            _setLocalizedStatus("Str_Mods_InvalidAudioFormat", [ex.Message]);
        }
    }

    public void RefreshModStatus()
    {
        try
        {
            string fontPath = Path.Combine(_paths.Modifications, @"content\fonts\CustomFont.ttf");
            _fontStatusKey = File.Exists(fontPath) ? "Str_Mods_CustomFontActive" : "Str_Mods_DefaultFontStatus";
            _fontStatusArgs = Array.Empty<object?>();
            CurrentFontStatus = LocalizedFormatHelper.Format(_fontStatusKey, _fontStatusArgs);

            var detectedCursor = CursorPresetHandler.DetectCursorPreset(_paths.Modifications);
            if (_selectedCursorPreset != detectedCursor)
            {
                _selectedCursorPreset = detectedCursor;
                OnPropertyChanged(nameof(SelectedCursorPreset));
            }

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
        catch (Exception ex)
        {
            Logger.ExceptionStatic("ModsViewModel.RefreshModStatus", ex);
        }
    }

    public void RefreshLocalizedProperties()
    {
        CurrentFontStatus = LocalizedFormatHelper.Format(_fontStatusKey, _fontStatusArgs);
        OnPropertyChanged(nameof(CustomDeathSoundStatusText));
    }
}
