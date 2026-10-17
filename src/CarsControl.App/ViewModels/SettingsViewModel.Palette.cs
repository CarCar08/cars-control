using CarsControl.App.Services;
using CarsControl.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

/// <summary>Settings › Command palette.</summary>
public sealed partial class SettingsViewModel
{
    private GlobalHotkey _hotkey = null!;

    [ObservableProperty] private bool _paletteGlobal;
    [ObservableProperty] private string _paletteHotkey = "";
    [ObservableProperty] private string _paletteStatus = "";
    [ObservableProperty] private bool _paletteProblem;

    private void InitPalette(GlobalHotkey hotkey)
    {
        _hotkey = hotkey;
        _hotkey.Changed += UpdatePaletteStatus;
    }

    private void ReloadPalette()
    {
        var p = _settings.Store.Current.Palette;
        PaletteGlobal = p.GlobalHotkey;
        PaletteHotkey = p.Hotkey;
        UpdatePaletteStatus();
    }

    private void UpdatePaletteStatus()
    {
        var p = _settings.Store.Current.Palette;
        PaletteProblem = _hotkey.Problem is not null;
        PaletteStatus = _hotkey.Problem
                        ?? (_hotkey.IsRegistered ? $"{p.Hotkey} opens the palette from anywhere, even while C.A.R.S. is in the tray."
                            : !p.GlobalHotkey ? "Only Ctrl+K inside C.A.R.S. opens it."
                            : App.IsDevInstance ? "Test copies leave the shortcut to the installed app."
                            : "Not registered yet.");
    }

    partial void OnPaletteGlobalChanged(bool value) => SavePalette(p => p.GlobalHotkey = value);

    /// <summary>From the shortcut recorder (press the keys in the box).</summary>
    partial void OnPaletteHotkeyChanged(string value)
    {
        if (!_loading && GlobalHotkey.TryParse(value, out _, out _)) SavePalette(p => p.Hotkey = value);
        else if (!_loading)
        {
            PaletteProblem = true;
            PaletteStatus = $"\"{value}\" needs Ctrl, Alt or Win plus a key.";
        }
    }

    private void SavePalette(Action<Core.Settings.PaletteSettings> change)
    {
        if (_loading) return;
        _settings.Store.Update(s => change(s.Palette));
        _hotkey.Apply(_settings.Store.Current.Palette);
    }

    [RelayCommand]
    private static void OpenPalette() => PaletteWindow.Open();

    [RelayCommand]
    private void ResetPaletteUsage()
    {
        _settings.Store.Update(s => s.Palette.Usage.Clear());
        Message = "The palette forgot what you use most; results are ranked by match only until you use it again.";
    }
}
