using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Core.Elevation;
using CarsControl.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed record GameRow(GameEntry Game, string Drive)
{
    public string Name => Game.Name;
    public long Bytes => Game.SizeBytes;
    public string SizeText => Format.Bytes(Game.SizeBytes);
    /// <summary>Only Steam records when a game was last played; for other launchers "unknown" must not read as "never".</summary>
    public bool PlayKnown => Game.LastPlayed is not null || Game.Source == "Steam";
    public string PlayedText => Game.LastPlayed is { } lp ? $"{lp:yyyy-MM-dd} ({Format.Ago(lp)})" : PlayKnown ? "never" : "not reported";
    public int Minutes => Game.PlaytimeMinutes ?? 0;
    public string HoursText => Minutes <= 0 ? "—" : Minutes < 60 ? $"{Minutes} min" : $"{Minutes / 60.0:F0} h";

    /// <summary>Never-played games count as the longest ago; games whose launcher doesn't report it sort after those.</summary>
    public double DaysSincePlayed => Game.LastPlayed is { } lp ? (DateTime.Now - lp).TotalDays : PlayKnown ? double.MaxValue : -1;

    public bool Stale => PlayKnown && DaysSincePlayed > 90;

    /// <summary>An Epic game moved by C.A.R.S. whose other copy still exists (see <see cref="EpicMover.OldCopyOf"/>).</summary>
    public string? ItemFile { get; init; }
    public (string Old, string New, bool Reverted)? MovedCopy { get; init; }
    public bool HasMovedCopy => MovedCopy is not null;
    public string MovedCopyButton => MovedCopy is { Reverted: true } ? "Remove unused copy" : "Remove old copy";
    public string MovedCopyTip => MovedCopy switch
    {
        { Reverted: true } m => $"Epic switched this game back to {m.Old}, so the copy at {m.New} isn't used. Removes that copy.",
        { } m => $"Removes the old copy at {m.Old} after checking it matches the one Epic now uses ({m.New}). Do this once the game has started from Epic without downloading.",
        _ => "",
    };
    public string Library => Game.Library;
    public string Source => Game.Source;

    /// <summary>Steam games need an app id; Epic games use their installed manifest.</summary>
    public bool CanMove => Game.InstallPath is not null && (Game.Source == "Epic" || Game.Source == "Steam" && Game.SteamAppId is not null);
    public override string ToString() => Game.Name;
}

/// <summary>Storage › Games: search, launcher and library chips, a "last played" filter and one sort.</summary>
public sealed partial class StorageViewModel
{
    private IReadOnlyList<GameEntry> _games = [];
    private IReadOnlyList<GameLibrary> _libraries = [];

    /// <summary>Every game from the libraries (the command palette searches them).</summary>
    public IReadOnlyList<GameEntry> AllGames => _games;
    private List<GameRow> _gameRows = [];
    private bool _gamesPending;
    private bool _syncingGameSort;

    public BulkObservableCollection<GameRow> Games { get; } = [];
    public ObservableCollection<FilterChip> GameSourceChips { get; } = [];
    public ObservableCollection<FilterChip> GameLibraryChips { get; } = [];

    public IReadOnlyList<Choice<int>> GamePlayedOptions { get; } =
    [
        new(0, "Played any time"), new(30, "Not played in 30+ days"), new(90, "Not played in 90+ days"),
        new(365, "Not played in a year"), new(-1, "Never played"),
    ];

    public IReadOnlyList<SortOption> GameSortOptions { get; } =
    [
        new("Bytes", "Size", true), new("DaysSincePlayed", "Not played longest", true), new("Minutes", "Hours played", true),
        new("Name", "Name", false), new("Source", "Launcher", false), new("Library", "Library", false), new("Drive", "Drive", false),
    ];

    [ObservableProperty] private string _gameSearch = "";
    [ObservableProperty] private Choice<int> _gamePlayed = null!;
    [ObservableProperty] private string _gameSortKey = "Bytes";
    [ObservableProperty] private bool _gameSortDescending = true;
    [ObservableProperty] private SortOption? _selectedGameSort;
    [ObservableProperty] private string? _gamesSummary;
    [ObservableProperty] private bool _hasGameFilters;

    private void LoadGameChoices()
    {
        GamePlayed = GamePlayedOptions[0];
        if (ParseSort(_settings.GetUi("storage.games.sort")) is { } s && GameSortOptions.Any(o => o.Key == s.Key))
        {
            GameSortKey = s.Key;
            GameSortDescending = s.Descending;
        }
        SelectedGameSort = GameSortOptions.First(o => o.Key == GameSortKey);
    }

    partial void OnGameSearchChanged(string value) => GamesChanged();
    partial void OnGamePlayedChanged(Choice<int> value) => GamesChanged();
    partial void OnGameSortKeyChanged(string value) => GameSortChanged();
    partial void OnGameSortDescendingChanged(bool value) => GameSortChanged();

    partial void OnSelectedGameSortChanged(SortOption? value)
    {
        if (value is null || _syncingGameSort || _loading || value.Key == GameSortKey) return;
        _syncingGameSort = true;
        GameSortKey = value.Key;
        GameSortDescending = value.DescendingFirst;
        _syncingGameSort = false;
        GameSortChanged();
    }

    private void GameSortChanged()
    {
        if (_syncingGameSort || _loading) return;
        _syncingGameSort = true;
        SelectedGameSort = GameSortOptions.FirstOrDefault(o => o.Key == GameSortKey);
        _syncingGameSort = false;
        _settings.SetUi("storage.games.sort", $"{GameSortKey}:{(GameSortDescending ? "desc" : "asc")}");
        GamesChanged();
    }

    [RelayCommand]
    private void ToggleGameSortDirection() => GameSortDescending = !GameSortDescending;

    private void GamesChanged()
    {
        if (_loading) return;
        Later(ref _gamesPending, () =>
        {
            _gamesPending = false;
            ApplyGameFilters();
        });
    }

    private void SetGames(IReadOnlyList<GameEntry> games, IReadOnlyList<GameLibrary> libraries)
    {
        _games = games;
        _libraries = libraries;
        _gameRows = games.Select(g =>
        {
            var row = new GameRow(g, DriveOf(g.InstallPath is { Length: >= 2 } p ? p : g.Library is { Length: >= 2 } l ? l : "?:"));
            if (g.Source != "Epic" || g.InstallPath is null || EpicMover.FindItemFile(g.InstallPath) is not { } item) return row;
            return row with { ItemFile = item, MovedCopy = EpicMover.OldCopyOf(item) };
        }).ToList();

        var offSources = _settings.GetUi("storage.games.sources.off")?.Split(',').ToHashSet() ?? [];
        foreach (var c in GameSourceChips) c.PropertyChanged -= OnGameChipChanged;
        GameSourceChips.Clear();
        foreach (var g in _gameRows.GroupBy(r => r.Source).OrderByDescending(g => g.Count()))
        {
            var chip = new FilterChip(g.Key, g.Key) { Detail = g.Count().ToString(), IsSelected = !offSources.Contains(g.Key) };
            chip.PropertyChanged += OnGameChipChanged;
            GameSourceChips.Add(chip);
        }

        var offLibs = _settings.GetUi("storage.games.libraries.off")?.Split('|').ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var c in GameLibraryChips) c.PropertyChanged -= OnGameChipChanged;
        GameLibraryChips.Clear();
        foreach (var l in libraries)
        {
            var chip = new FilterChip(l.Path, $"{l.Path}")
            {
                Detail = $"{l.Games} · {Format.Bytes(l.Bytes)}",
                ToolTip = l.Online ? $"{l.Source} library · {l.Games} games" : $"{l.Source} library on a drive that isn't connected",
                IsAvailable = l.Online && l.Games > 0,
                IsSelected = !offLibs.Contains(l.Path),
            };
            chip.PropertyChanged += OnGameChipChanged;
            GameLibraryChips.Add(chip);
        }
        ApplyGameFilters();
    }

    private void OnGameChipChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FilterChip.IsSelected)) return;
        _settings.SetUi("storage.games.sources.off", string.Join(',', GameSourceChips.Where(c => !c.IsSelected).Select(c => c.Key)));
        _settings.SetUi("storage.games.libraries.off", string.Join('|', GameLibraryChips.Where(c => !c.IsSelected).Select(c => c.Key)));
        GamesChanged();
    }

    private void ApplyGameFilters()
    {
        var sources = GameSourceChips.Where(c => c.IsSelected).Select(c => c.Key).ToHashSet();
        var offLibraries = GameLibraryChips.Where(c => !c.IsSelected).Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<GameRow> q = _gameRows.Where(r => sources.Contains(r.Source) && !offLibraries.Contains(r.Library));
        if (GameSearch.Trim() is { Length: > 0 } text) q = q.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
        q = GamePlayed.Value switch
        {
            -1 => q.Where(r => r.Game.LastPlayed is null && r.PlayKnown),
            > 0 and var days => q.Where(r => r.DaysSincePlayed >= days),
            _ => q,
        };

        Func<GameRow, object> key = GameSortKey switch
        {
            "DaysSincePlayed" => r => r.DaysSincePlayed,
            "Minutes" => r => r.Minutes,
            "Name" => r => r.Name,
            "Source" => r => r.Source,
            "Library" => r => r.Library,
            "Drive" => r => r.Drive,
            _ => r => r.Bytes,
        };
        IComparer<object> cmp = Comparer<object>.Create((a, b) => a is string sa && b is string sb
            ? StringComparer.CurrentCultureIgnoreCase.Compare(sa, sb)
            : Comparer<object>.Default.Compare(a, b));
        var rows = (GameSortDescending ? q.OrderByDescending(key, cmp) : q.OrderBy(key, cmp)).ThenByDescending(r => r.Bytes).ToList();
        Games.ReplaceAll(rows);

        HasGameFilters = GameSearch.Trim().Length > 0 || GamePlayed.Value != 0 || GameSourceChips.Any(c => !c.IsSelected) || GameLibraryChips.Any(c => c.IsAvailable && !c.IsSelected);
        var stale = rows.Where(r => r.Stale).Sum(r => r.Bytes);
        GamesSummary = (HasGameFilters ? $"Showing {rows.Count} of {_gameRows.Count} games" : $"{rows.Count} games")
                       + $" · {Format.Bytes(rows.Sum(r => r.Bytes))}"
                       + (stale > 0 ? $" · {Format.Bytes(stale)} not played in 90+ days" : "");
    }

    [RelayCommand]
    private void ResetGameFilters()
    {
        GameSearch = "";
        GamePlayed = GamePlayedOptions[0];
        foreach (var c in GameSourceChips.Concat(GameLibraryChips)) c.IsSelected = true;
    }

    /// <summary>Totals for the Home dashboard (same numbers as this page, unfiltered).</summary>
    public (int Count, long Bytes, long StaleBytes) GameTotals() =>
        (_gameRows.Count, _gameRows.Sum(g => g.Bytes), _gameRows.Where(g => g.Stale).Sum(g => g.Bytes));

    /// <summary>Moves a Steam or Epic game (a window explains exactly what happens first).</summary>
    [RelayCommand]
    private async Task MoveGameAsync(GameRow? row)
    {
        if (row is null) return;
        if (row.Game.Source == "Epic" && row.Game.InstallPath is not null)
        {
            var epicChanged = Platform.ShowMove(MoveViewModel.ForEpicGame(row.Game, () => { }));
            if (epicChanged) await LoadReportsAsync();
            return;
        }
        if (!row.CanMove)
        {
            await Dialogs.InfoAsync("Only Steam and Epic games can be moved from here. For other launchers, use the launcher's own move or reinstall option.");
            return;
        }
        var changed = Platform.ShowMove(MoveViewModel.ForGame(row.Game, _libraries, () => { }));
        if (changed) await LoadReportsAsync();
    }

    /// <summary>After an Epic move: removes the copy Epic no longer uses (the old one, or the new one if Epic undid the move).</summary>
    [RelayCommand]
    private async Task RemoveMovedCopyAsync(GameRow? row)
    {
        if (row?.ItemFile is not { } item || row.MovedCopy is not { } m) return;
        var question = m.Reverted
            ? $"Epic switched {row.Name} back to {m.Old}, so the copy at {m.New} isn't used.\n\nRemove {m.New}?"
            : $"Remove the old copy of {row.Name} at {m.Old}?\n\nOnly do this after the game has started from Epic without downloading again. The copy at {m.New} is checked against it first; if they differ, nothing is removed.";
        if (!await Dialogs.ConfirmAsync(question, "Remove", title: m.Reverted ? "Remove unused copy" : "Remove old copy")) return;
        var why = await EpicMover.RemoveOldCopyAsync(item, async manifestId =>
        {
            try
            {
                var r = await _helper.SendAsync(new CarsControl.Contracts.RemoveMovedGameOp(manifestId), null, HelperClient.DevUnelevated);
                return r.Ok ? null : r.Message;
            }
            catch (Exception ex) when (ex is HelperCancelledException or HelperUnavailableException) { return "admin approval was cancelled"; }
        }, CancellationToken.None);
        if (why is null) await Dialogs.InfoAsync($"Removed the {(m.Reverted ? "unused" : "old")} copy of {row.Name}.");
        else await Dialogs.InfoAsync($"Nothing more was removed: {why}");
        await LoadReportsAsync();
    }

    [RelayCommand]
    private static async Task UninstallGameAsync(GameRow? row)
    {
        if (row?.Game.SteamAppId is { } id)
            Platform.OpenUri($"steam://uninstall/{id}");
        else if (row?.Game.Source == "Epic")
            Platform.OpenUri("com.epicgames.launcher://store/library");
        else if (row is not null)
            await Dialogs.InfoAsync("Uninstall this game from its launcher, or from the Apps page.");
    }
}
