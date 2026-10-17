using CarsControl.App.Services;
using CarsControl.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

/// <summary>Settings › Gaming mode.</summary>
public sealed partial class SettingsViewModel
{
    private GameMode _game = null!;
    private GamingEffects _gaming = null!;
    private GameWatcher _watcher = null!;

    [ObservableProperty] private bool _gamingAutomatic;
    [ObservableProperty] private bool _gamingPauseWindowsUpdate;
    [ObservableProperty] private bool _gamingPauseSearch;
    [ObservableProperty] private bool _gamingFreeStandby;
    [ObservableProperty] private bool _gamingMaintenanceAfter;
    [ObservableProperty] private bool _gamingApproveAtSignIn;
    [ObservableProperty] private string _gamingExtraGames = "";
    [ObservableProperty] private string _gamingIgnored = "";
    [ObservableProperty] private bool _isGaming;
    [ObservableProperty] private string _gamingStatus = "";
    [ObservableProperty] private string _gamingSessionStatus = "";
    [ObservableProperty] private bool _gamingSessionActive;
    [ObservableProperty] private string? _gamingLastSession;

    /// <summary>"wallpaper64.exe" while it's what turned Gaming mode on.</summary>
    [ObservableProperty] private string? _gamingDetectedProgram;

    private void InitGaming(GameMode game, GamingEffects gaming, GameWatcher watcher)
    {
        _game = game;
        _gaming = gaming;
        _watcher = watcher;
        _game.Changed += UpdateGamingStatus;
        _gaming.Changed += UpdateGamingStatus;
        _helper.ConnectionChanged += _ => Platform.OnUiThread(UpdateGamingStatus);
    }

    private void ReloadGaming()
    {
        var g = _settings.Store.Current.Gaming;
        GamingAutomatic = g.Automatic;
        GamingPauseWindowsUpdate = g.PauseWindowsUpdate;
        GamingPauseSearch = g.PauseSearchIndexing;
        GamingFreeStandby = g.FreeStandbyMemory;
        GamingMaintenanceAfter = g.MaintenanceAfterPlay;
        GamingApproveAtSignIn = g.ApproveAtSignIn;
        GamingExtraGames = string.Join(Environment.NewLine, g.ExtraGames);
        GamingIgnored = string.Join(Environment.NewLine, g.IgnoredPrograms);
        UpdateGamingStatus();
    }

    private void UpdateGamingStatus()
    {
        IsGaming = _game.IsOn;
        var known = _watcher.KnownGames;
        GamingStatus = _game.IsOn
            ? $"Gaming mode is {_game.Describe().ToLowerInvariant().Replace("on — ", "on: ")}" + (_game.Since is { } since ? $" since {since:HH:mm}" : "")
            : $"Gaming mode is off" + (known > 0 ? $" · watching for {known} games from your libraries" : "");
        GamingSessionActive = _helper.IsConnected;
        GamingSessionStatus = _helper.IsConnected ? "Admin session active: pausing Windows Update and indexing and freeing memory will work."
            : _gaming.ApprovalDeclined ? "You declined the admin prompt, so those three are skipped until you approve."
            : "No admin session yet, so those three are skipped until you approve (it's asked once at sign-in).";
        GamingLastSession = _gaming.LastSessionText;
        GamingDetectedProgram = _game.DetectedGame is not null ? _watcher.DetectedProgram : null;
    }

    /// <summary>The detected program isn't a game: never count it again, and switch off now.</summary>
    [RelayCommand]
    private void NotAGame()
    {
        if (GamingDetectedProgram is not { } program) return;
        _settings.Store.Update(s =>
        {
            if (!s.Gaming.IgnoredPrograms.Contains(program, StringComparer.OrdinalIgnoreCase)) s.Gaming.IgnoredPrograms.Add(program);
        });
        _loading = true;
        GamingIgnored = string.Join(Environment.NewLine, _settings.Store.Current.Gaming.IgnoredPrograms);
        _loading = false;
        _game.SetDetected(null);
        Message = $"{program} won't turn Gaming mode on again (it's in \"Never count as games\").";
    }

    partial void OnGamingAutomaticChanged(bool value) => SaveGaming(g => g.Automatic = value);
    partial void OnGamingPauseWindowsUpdateChanged(bool value) => SaveGaming(g => g.PauseWindowsUpdate = value);
    partial void OnGamingPauseSearchChanged(bool value) => SaveGaming(g => g.PauseSearchIndexing = value);
    partial void OnGamingFreeStandbyChanged(bool value) => SaveGaming(g => g.FreeStandbyMemory = value);
    partial void OnGamingMaintenanceAfterChanged(bool value) => SaveGaming(g => g.MaintenanceAfterPlay = value);
    partial void OnGamingApproveAtSignInChanged(bool value) => SaveGaming(g => g.ApproveAtSignIn = value);
    partial void OnGamingExtraGamesChanged(string value) => SaveGaming(g => g.ExtraGames = Lines(value));
    partial void OnGamingIgnoredChanged(string value) => SaveGaming(g => g.IgnoredPrograms = Lines(value));

    /// <summary>One program per line; "Game.exe" or a full path both work (only the file name is kept).</summary>
    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => System.IO.Path.GetFileName(l.Trim('"')))
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void SaveGaming(Action<GamingSettings> change)
    {
        if (_loading) return;
        _settings.Store.Update(s => change(s.Gaming));
        UpdateGamingStatus();
    }

    [RelayCommand]
    private void ToggleGaming() => _game.SetManual(!_game.IsOn);

    [RelayCommand]
    private async Task ApproveGamingSessionAsync()
    {
        GamingSessionStatus = "Waiting for the admin prompt…";
        await _gaming.ApproveSessionAsync();
        UpdateGamingStatus();
    }
}
