using System.Collections.ObjectModel;
using System.IO;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Winget;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class GetAppRowViewModel(WingetResult result) : ObservableObject
{
    public WingetResult Result { get; } = result;
    public string Name => Result.Name;
    public string Id => Result.Id;
    public string Publisher => Result.Publisher;
    public string Version => Result.Version ?? "";
    public string SourceText => Result.IsStore ? "Microsoft Store" : "winget";
    public bool IsInstalled => Result.IsInstalled;
    public string InstalledText => Result.InstalledVersion is { } v ? $"Installed ({v})" : "";
    public override string ToString() => $"{Name} by {Publisher}";

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _failed;
}

/// <summary>
/// Apps › Get apps: search winget and the Microsoft Store, tick what you want, install it for just you (no admin) or for
/// everyone on the PC (admin helper). Lists can be saved and opened again, so a new PC takes one click. winget does the
/// installing, with its own hash checks; C.A.R.S. only passes ids it has validated.
/// </summary>
public sealed partial class GetAppsViewModel : ObservableObject
{
    private readonly HelperClient _helper;
    private readonly CarsDb _db;
    private readonly InventoryStore _inventory;
    private CancellationTokenSource? _cts;

    public ObservableCollection<GetAppRowViewModel> Results { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _includeStore = true;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _status = "Type a name and press Enter.";
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private GetAppRowViewModel? _current;
    [ObservableProperty] private string? _detail;
    [ObservableProperty] private string? _detailLink;

    public GetAppsViewModel(HelperClient helper, CarsDb db, InventoryStore inventory)
    {
        _helper = helper;
        _db = db;
        _inventory = inventory;
    }

    public bool HasSelection => SelectedCount > 0;

    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(HasSelection));

    private void Notice(string text, NoticeKind kind)
    {
        Banner = text;
        BannerKind = kind;
    }

    // ---------- search ----------

    [RelayCommand]
    private async Task SearchAsync()
    {
        var q = Query.Trim();
        if (q.Length < 2) { Status = "Type at least two letters."; return; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsSearching = true;
        Status = $"Looking for “{q}”…";
        try
        {
            var found = await WingetSearch.SearchAsync(q, 40, IncludeStore, ct);
            Show(found.Select(f => new GetAppRowViewModel(f)));
            Status = found.Count == 0 ? $"Nothing matches “{q}”." : $"{found.Count} result{(found.Count == 1 ? "" : "s")} for “{q}”.";
        }
        catch (OperationCanceledException)
        {
            // A newer search replaced this one.
        }
        catch (WingetUnavailableException ex)
        {
            Status = "";
            Notice("winget isn't available: " + ex.Message, NoticeKind.Error);
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void Show(IEnumerable<GetAppRowViewModel> rows)
    {
        foreach (var old in Results) old.PropertyChanged -= OnRowChanged;
        Results.Clear();
        foreach (var r in rows)
        {
            r.PropertyChanged += OnRowChanged;
            Results.Add(r);
        }
        SelectedCount = 0;
        Current = null;
        Detail = null;
        DetailLink = null;
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GetAppRowViewModel.IsSelected)) SelectedCount = Results.Count(r => r.IsSelected);
    }

    partial void OnCurrentChanged(GetAppRowViewModel? value)
    {
        Detail = null;
        DetailLink = null;
        if (value is not null) _ = LoadDetailAsync(value);
    }

    private async Task LoadDetailAsync(GetAppRowViewModel row)
    {
        var info = await WingetShow.ShowAsync(row.Id);
        if (!ReferenceEquals(Current, row) || info is null) return;
        Detail = string.Join("\n", new[] { info.Description, info.License is { } l ? $"License: {l}" : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
        DetailLink = info.Homepage ?? info.PublisherUrl;
    }

    [RelayCommand]
    private void ShowDetail(GetAppRowViewModel? row) => Current = Current == row ? null : row;

    [RelayCommand]
    private void OpenDetailLink()
    {
        if (DetailLink is { } url) Platform.OpenUri(url);
    }

    // ---------- install ----------

    [RelayCommand]
    private Task InstallForMeAsync() => InstallSelectedAsync(allUsers: false);

    [RelayCommand]
    private Task InstallForEveryoneAsync() => InstallSelectedAsync(allUsers: true);

    [RelayCommand]
    private void CancelInstall() => _cts?.Cancel();

    private async Task InstallSelectedAsync(bool allUsers)
    {
        var chosen = Results.Where(r => r.IsSelected && !r.IsInstalled).ToList();
        if (chosen.Count == 0)
        {
            Notice(Results.Any(r => r.IsSelected) ? "Those are already installed." : "Tick the apps you want first.", NoticeKind.Info);
            return;
        }
        var list = string.Join("\n", chosen.Take(10).Select(r => $"• {r.Name} ({r.SourceText})")) + (chosen.Count > 10 ? $"\n• and {chosen.Count - 10} more" : "");
        var who = allUsers ? "for every account on this PC (needs admin approval once)" : "just for you (no admin needed)";
        if (!await Dialogs.ConfirmAsync($"Install {chosen.Count} app{(chosen.Count == 1 ? "" : "s")} {who}?\n\n{list}\n\nwinget downloads each one from its publisher and checks it against the hash in its catalog.", "Install")) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsInstalling = true;
        int ok = 0, failed = 0;
        try
        {
            foreach (var row in chosen)
            {
                ct.ThrowIfCancellationRequested();
                var success = await InstallOneAsync(row, allUsers, ct);
                if (success) ok++;
                else failed++;
            }
        }
        catch (OperationCanceledException)
        {
            Notice("Stopped. What already installed stays installed.", NoticeKind.Warning);
        }
        finally
        {
            IsInstalling = false;
        }
        if (ok + failed > 0)
            Notice($"{ok} installed" + (failed > 0 ? $", {failed} didn't install (the line under each says why)." : "."), failed == 0 ? NoticeKind.Success : NoticeKind.Warning);
        if (ok > 0) _ = _inventory.RefreshAsync();
    }

    private async Task<bool> InstallOneAsync(GetAppRowViewModel row, bool allUsers, CancellationToken ct)
    {
        if (Core.Inventory.ProtectedApps.IsAlwaysProtectedName(row.Id) || Core.Inventory.ProtectedApps.IsAlwaysProtectedName(row.Name))
        { row.Failed = true; row.Status = "Salad is permanently protected; use its own installer when you choose."; return false; }
        row.IsBusy = true;
        row.Failed = false;
        row.Status = "Starting…";
        void Line(string text) => Platform.OnUiThread(() => row.Status = text.Length > 100 ? text[..100] + "…" : text);
        try
        {
            if (allUsers)
            {
                var r = await _helper.SendAsync(new WingetInstallOp(row.Id, row.Result.Source), Line, HelperClient.DevUnelevated, ct);
                return Finish(row, r.Ok, r.Message, allUsers, r.Ok ? null : WingetFailureOf(r), r.LogPath);
            }

            if (WingetCli.Locate() is not { } winget)
            {
                row.Status = "winget wasn't found.";
                row.Failed = true;
                return false;
            }
            if (HelperClient.DevUnelevated)
            {
                row.Status = "Test copy: installs are dry runs.";
                return false;
            }
            var args = WingetCli.InstallArgs(row.Id, row.Result.Source, "user", null, null);
            var (outcome, raw) = await WingetCli.RunAsync(winget, args, Line, ct);
            return Finish(row, outcome.Ok, outcome.Message, allUsers, outcome.Failure, null);
        }
        catch (HelperCancelledException)
        {
            row.Status = "Admin approval was cancelled.";
            row.Failed = true;
            return false;
        }
        catch (HelperUnavailableException ex)
        {
            row.Status = ex.Message;
            row.Failed = true;
            return false;
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private static WingetFailure? WingetFailureOf(OpResult r) => r.Message?.Contains("no installer", StringComparison.OrdinalIgnoreCase) == true ? WingetFailure.NoApplicableInstaller : null;

    private bool Finish(GetAppRowViewModel row, bool ok, string? message, bool allUsers, WingetFailure? failure, string? log)
    {
        row.Failed = !ok;
        row.Status = ok ? "Installed." : failure == WingetFailure.NoApplicableInstaller && !allUsers
            ? "This one has no just-for-you installer. Use “Install for everyone” instead."
            : message ?? "winget didn't finish.";
        _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "install", $"winget:{row.Id}", row.Name, row.SourceText, null, row.Version.Length > 0 ? row.Version : null, allUsers,
            ok ? "Succeeded" : "Failed", ok ? 0 : 1, ok ? $"Installed {(allUsers ? "for every account" : "for you")}" : row.Status, null));
        return ok;
    }

    // ---------- lists ----------

    [RelayCommand]
    private async Task SaveListAsync()
    {
        var chosen = Results.Where(r => r.IsSelected).Select(r => new AppListEntry(r.Id, r.Name, r.Result.Source)).ToList();
        if (chosen.Count == 0)
        {
            Notice("Tick the apps to put in the list first.", NoticeKind.Info);
            return;
        }
        await WriteListAsync(chosen, "my apps");
    }

    [RelayCommand]
    private async Task SaveInstalledAsync()
    {
        var apps = _inventory.Current?.Apps.Where(a => a.WingetId is not null && a.WingetSource is "winget" or "msstore")
            .Select(a => new AppListEntry(a.WingetId!, a.Name, a.WingetSource!)).DistinctBy(a => a.Id).ToList() ?? [];
        if (apps.Count == 0)
        {
            Notice("None of the installed apps are known to winget yet (the Apps tab is still loading, or none match).", NoticeKind.Info);
            return;
        }
        await WriteListAsync(apps, "installed apps");
    }

    private async Task WriteListAsync(IReadOnlyList<AppListEntry> apps, string what)
    {
        var path = Platform.PickSaveFile("Save the app list", $"CARS app list {DateTime.Now:yyyy-MM-dd}.json", "App list (*.json)|*.json");
        if (path is null) return;
        try
        {
            await File.WriteAllTextAsync(path, AppList.Serialize(apps));
            Notice($"Saved {apps.Count} {what} to {Path.GetFileName(path)}. Open it on a new PC to install them all.", NoticeKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice("Couldn't save the list: " + ex.Message, NoticeKind.Error);
        }
    }

    [RelayCommand]
    private async Task OpenListAsync()
    {
        var path = Platform.PickOpenFile("Open an app list", "App list or winget export (*.json)|*.json|All files (*.*)|*.*");
        if (path is null) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 2_000_000) { Notice("That file is too big to be an app list.", NoticeKind.Error); return; }
            var apps = AppList.Parse(await File.ReadAllTextAsync(path), out var skipped);
            if (apps.Count == 0) { Notice("That file has no apps in it that C.A.R.S. can read.", NoticeKind.Warning); return; }

            var installed = _inventory.Current?.Apps.Where(a => a.WingetId is not null).Select(a => a.WingetId!).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            Show(apps.Select(a => new GetAppRowViewModel(new WingetResult(a.Id, a.Name, "", null, a.Source, installed.Contains(a.Id) ? "installed" : null))));
            foreach (var r in Results.Where(r => !r.IsInstalled)) r.IsSelected = true;
            Status = $"{Path.GetFileName(path)}: {apps.Count} apps, {Results.Count(r => r.IsInstalled)} already here." + (skipped > 0 ? $" {skipped} entries were skipped (not valid winget ids)." : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice("Couldn't read that file: " + ex.Message, NoticeKind.Error);
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in Results) r.IsSelected = false;
    }
}
