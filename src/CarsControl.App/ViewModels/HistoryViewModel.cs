using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CarsControl.App.Services;
using CarsControl.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace CarsControl.App.ViewModels;

public sealed class HistoryRow(HistoryEntry e)
{
    public HistoryEntry Entry { get; } = e;
    public string When => Entry.Started.ToString("yyyy-MM-dd HH:mm");
    public string Title => Entry.Title;
    public string KindText => Entry.Kind switch { "update" => "Update", "uninstall" => "Uninstall", "cleanup" => "Cleanup", "leftovers" => "Leftovers", "maintenance" => "Maintenance", "gaming" => "Gaming", "privacy" => "Privacy", "health" => "Health", "startup" => "Startup", "backup" => "Backup", "sync" => "Sync", "install" => "Install", var k => k };
    public string SourceText => Entry.Imported ? $"{FriendlySource(Entry.Source)} (imported)" : FriendlySource(Entry.Source);

    /// <summary>Internal method names ("PsModule", "SteamHandoff") → words.</summary>
    internal static string FriendlySource(string s) => s switch
    {
        "WindowsUpdate" => "Windows Update",
        "PsModule" => "PowerShell",
        "Pip" => "pip",
        "Npm" => "npm",
        "Wsl" => "WSL",
        "Winget" => "winget",
        "Msix" => "Store app",
        "SteamHandoff" => "Steam",
        "Msi" => "Windows Installer",
        "Quiet" or "DetectedSilent" => "Silent uninstaller",
        "Interactive" => "App's uninstaller",
        _ => s,
    };
    public string Versions => (Entry.FromVersion, Entry.ToVersion) switch
    {
        ({ } a, { } b) when a != b => $"{a} → {b}",
        ({ } a, _) => a,
        (_, { } b) => b,
        _ => "",
    };
    public string Status => Entry.Status;
    public bool IsFailure => Entry.Status is "Failed" or "Rejected" or "NotFound";
    public bool Elevated => Entry.Elevated;
    public string Message => Entry.Message ?? "";
    public string Output => string.IsNullOrWhiteSpace(Entry.Output) ? "(no output recorded)" : Entry.Output!;

    public override string ToString() => $"{Title} — {KindText} {Status}";

    /// <summary>Any app C.A.R.S. uninstalled can be checked for leftovers again later.</summary>
    public bool CanFindLeftovers => Entry.Kind == "uninstall" && Entry.Status == "Succeeded" && !Entry.Imported;

    /// <summary>Quarantined leftovers can be put back for 30 days.</summary>
    public bool CanRestore => Entry.Kind == "leftovers" && Entry.Status == "Succeeded" && Entry.Data is not null
                              && DateTime.Now - Entry.Started < TimeSpan.FromDays(30);
}

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly CarsDb _db;

    public ObservableCollection<HistoryRow> Rows { get; } = [];
    public ICollectionView View { get; }

    public IReadOnlyList<string> FilterOptions { get; } = ["Everything", "Failures only", "C.A.R.S. only (hide UniGetUI imports)"];
    public IReadOnlyList<string> RangeOptions { get; } = ["All time", "Today", "Last 7 days", "Last 30 days", "Last 90 days"];

    /// <summary>One chip per kind of operation; none selected means all of them.</summary>
    public ObservableCollection<FilterChip> Kinds { get; } = [];

    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private int _rangeIndex;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private HistoryRow? _selected;

    /// <summary>Output is fetched for the selected row only (the list query leaves it out).</summary>
    [ObservableProperty] private string _selectedOutput = "Select an operation to see its output.";

    partial void OnSelectedChanged(HistoryRow? value) =>
        SelectedOutput = value is null ? "Select an operation to see its output."
            : _db.GetOutput(value.Entry.Id) is { Length: > 0 } o ? o : "(no output recorded)";
    [ObservableProperty] private string _summary = "";

    /// <summary>What an empty list says: nothing recorded yet, or nothing matching the filters.</summary>
    [ObservableProperty] private string _emptyText = "";

    public HistoryViewModel(CarsDb db)
    {
        _db = db;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is HistoryRow r && FilterIndex switch
        {
            1 => r.IsFailure,
            2 => !r.Entry.Imported,
            _ => true,
        } && InRange(r) && KindSelected(r) && (Search.Trim() is not { Length: > 0 } q
              || r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || r.KindText.Contains(q, StringComparison.OrdinalIgnoreCase)
              || r.Message.Contains(q, StringComparison.OrdinalIgnoreCase) || r.SourceText.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private bool InRange(HistoryRow r) => RangeIndex switch
    {
        1 => r.Entry.Started.Date == DateTime.Today,
        2 => r.Entry.Started >= DateTime.Today.AddDays(-6),
        3 => r.Entry.Started >= DateTime.Today.AddDays(-29),
        4 => r.Entry.Started >= DateTime.Today.AddDays(-89),
        _ => true,
    };

    private bool KindSelected(HistoryRow r) => !Kinds.Any(k => k.IsSelected) || Kinds.Any(k => k.IsSelected && k.Key == r.Entry.Kind);

    partial void OnRangeIndexChanged(int value) => View.Refresh();
    partial void OnFilterIndexChanged(int value) => View.Refresh();
    partial void OnSearchChanged(string value) => View.Refresh();

    /// <summary>Saves what's shown (after filters and search) as a spreadsheet.</summary>
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var shown = View.Cast<HistoryRow>().Select(r => r.Entry).ToList();
        if (shown.Count == 0)
        {
            Summary = "Nothing to export with these filters.";
            return;
        }
        var path = Platform.PickSaveFile("Export history", $"CARS-history-{DateTime.Now:yyyy-MM-dd}.csv", "CSV file (*.csv)|*.csv");
        if (path is null) return;
        var csv = HistoryCsv.Build(shown, e => new HistoryRow(e).KindText, HistoryRow.FriendlySource);
        // UTF-8 with a byte-order mark: Excel then reads accents and arrows correctly.
        await System.IO.File.WriteAllTextAsync(path, csv, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Summary = $"Exported {shown.Count} rows to {path}";
        Platform.ShowInFileManager(path);
    }

    [RelayCommand]
    private void ResetFilters()
    {
        FilterIndex = 0;
        RangeIndex = 0;
        Search = "";
        foreach (var k in Kinds) k.IsSelected = false;
    }

    [RelayCommand]
    private void CopyOutput()
    {
        CarsControl.App.Services.Platform.CopyText(SelectedOutput);
    }

    [ObservableProperty] private string? _restoreMessage;

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (Selected is not { CanRestore: true } row) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(row.Entry.Data!);
            string? Get(string n) => doc.RootElement.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            var service = new CarsControl.Core.Uninstall.UninstallService(App.Services.GetRequiredService<CarsControl.Core.Elevation.HelperClient>());
            var log = new List<string>();
            var msg = await service.RestoreAsync(Get("user"), Get("machine"), log.Add, CancellationToken.None);
            _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "restore", row.Entry.TargetKey, row.Title, "Quarantine", null, null,
                Get("machine") is not null, "Succeeded", 0, msg, string.Join(Environment.NewLine, log)));
            RestoreMessage = msg;
        }
        catch (Exception ex)
        {
            RestoreMessage = $"Restore failed: {ex.Message}";
        }
        Reload();
    }

    /// <summary>Re-runs the leftover scan for an app removed earlier (e.g. a Steam game, whose uninstall
    /// used to skip the scan entirely). Found items go through the same review and quarantine.</summary>
    [RelayCommand]
    private async Task FindLeftoversAsync()
    {
        if (Selected is not { CanFindLeftovers: true } row) return;
        var store = App.Services.GetRequiredService<CarsControl.App.Services.InventoryStore>();
        if (store.Current is null) await store.RefreshAsync();
        var app = CarsControl.Core.Uninstall.RemovedApp.FromHistory(row.Entry.TargetKey, row.Title, row.Entry.Data);
        var others = (store.Current?.Apps ?? []).Where(a => a.Key != app.Key).ToList();
        var vm = UninstallViewModel.ForLeftovers(app, others, App.Services.GetRequiredService<CarsControl.Core.Elevation.HelperClient>(), _db);
        _ = vm.FindLeftoversAsync();
        CarsControl.App.Services.Platform.ShowUninstall(vm);
        Reload();
    }

    private static readonly string[] KindOrder = ["update", "cleanup", "uninstall", "leftovers", "maintenance", "gaming", "privacy", "restore"];

    /// <summary>Chips for the kinds that exist in the history (so a new kind needs no code here),
    /// keeping what was ticked across a reload.</summary>
    private void BuildKinds()
    {
        var ticked = Kinds.Where(k => k.IsSelected).Select(k => k.Key).ToHashSet();
        foreach (var k in Kinds) k.PropertyChanged -= OnKindChanged;
        Kinds.Clear();
        var kinds = Rows.GroupBy(r => r.Entry.Kind).OrderBy(g => Array.IndexOf(KindOrder, g.Key) is var i and >= 0 ? i : 99).ThenBy(g => g.Key);
        foreach (var g in kinds)
        {
            var chip = new FilterChip(g.Key, new HistoryRow(g.First().Entry).KindText) { Detail = g.Count().ToString(), IsSelected = ticked.Contains(g.Key) };
            chip.PropertyChanged += OnKindChanged;
            Kinds.Add(chip);
        }
    }

    private void OnKindChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterChip.IsSelected)) View.Refresh();
    }

    [RelayCommand]
    public void Reload()
    {
        Rows.Clear();
        foreach (var e in _db.ListHistory(1000)) Rows.Add(new HistoryRow(e));
        BuildKinds();
        var failures = Rows.Count(r => r.IsFailure);
        Summary = $"{Rows.Count} operations · {failures} failed · {Rows.Count(r => r.Entry.Imported)} imported from UniGetUI";
        EmptyText = Rows.Count == 0
            ? "Nothing here yet. Every update, uninstall, cleanup and move C.A.R.S. does is recorded here with its full output, so you can check what happened and put back removed leftovers."
            : "Nothing matches these filters. Reset filters shows everything.";
    }
}
