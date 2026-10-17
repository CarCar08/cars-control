using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Inventory;
using CarsControl.Core.Models;
using CarsControl.Core.Uninstall;
using CarsControl.Core.Updates;
using CarsControl.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class AppsViewModel : ObservableObject
{
    private readonly InventoryStore _store;
    private readonly HelperClient _helper;
    private readonly CarsDb _db;
    private readonly AppSettings _settings;
    private readonly UpdateCenter _updates;
    private readonly Dictionary<string, AppItemViewModel> _byKey = new();
    private readonly DispatcherTimer _searchDebounce;

    public ObservableCollection<AppItemViewModel> Items { get; } = [];
    public ICollectionView View { get; }

    public IReadOnlyList<string> ScopeOptions { get; } = ["All scopes", "Just me", "All users"];
    public IReadOnlyList<string> KindOptions { get; } = ["Everything", "Programs", "Store apps", "Games", "Drivers & system"];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _scopeIndex;
    [ObservableProperty] private int _kindIndex;
    [ObservableProperty] private bool _updatesOnly;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _summary = "Loading…";
    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected))]
    private AppItemViewModel? _selected;

    [ObservableProperty] private bool _isDetailsOpen;

    public bool HasSelected => Selected is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChecked), nameof(CheckedSummary))]
    [NotifyCanExecuteChangedFor(nameof(UninstallSelectedCommand))]
    private int _checkedCount;

    public bool HasChecked => CheckedCount > 0;

    public string CheckedSummary
    {
        get
        {
            var checkedItems = Items.Where(i => i.IsChecked).ToList();
            if (checkedItems.Count == 0) return "";
            var size = checkedItems.Sum(i => Math.Max(0, i.SizeBytes));
            // Same routing the uninstall window uses: a Steam game is handed to Steam (no admin) even
            // though its registry entry is machine-wide; round 2 said "1 need admin" then "no admin needed".
            var admin = checkedItems.Count(i => UninstallPlanner.Plan(i.Entry).Elevated);
            return $"{checkedItems.Count} selected · {Format.Bytes(size)}" + (admin > 0 ? $" · {admin} need admin" : "");
        }
    }

    public AppsViewModel(InventoryStore store, IconLoader icons, HelperClient helper, CarsDb db, AppSettings settings, UpdateCenter updates)
    {
        _store = store;
        _helper = helper;
        _db = db;
        _settings = settings;
        _updates = updates;
        AppItemViewModel.IconProvider = icons.LoadAsync;

        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Filter;
        View.SortDescriptions.Add(new SortDescription(nameof(AppItemViewModel.Name), ListSortDirection.Ascending));

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); View.Refresh(); };

        _store.Updated += Reload;
        _store.SizeMeasured += OnSizeMeasured;
        _store.LoadingChanged += v => IsLoading = v;
        IsLoading = _store.IsLoading;
        if (_store.Current is not null) Reload();
    }

    partial void OnSearchTextChanged(string value) { _searchDebounce.Stop(); _searchDebounce.Start(); }
    partial void OnScopeIndexChanged(int value) => View.Refresh();
    partial void OnKindIndexChanged(int value) => View.Refresh();
    partial void OnUpdatesOnlyChanged(bool value) => View.Refresh();
    partial void OnSelectedChanged(AppItemViewModel? value) => LoadLastAction(value);

    /// <summary>Merges a new snapshot into the existing rows (update / add / remove by key) instead of
    /// rebuilding the list: no jump to the top, selection and ticks survive, icons aren't reloaded.</summary>
    private void Reload()
    {
        Error = _store.LastError is { } e ? $"winget couldn't be read, so update info is missing: {e}" : null;
        var snap = _store.Current;
        if (snap is null) return;
        var sw = Stopwatch.StartNew();

        // No View.DeferRefresh() here: WPF's ListCollectionView throws ("Cannot change … while Refresh is
        // being deferred") if the source collection changes inside a deferral. Only rows that actually
        // appear/disappear raise collection changes; everything else is updated in place.
        var incoming = snap.Apps.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var gone in _byKey.Keys.Where(k => !incoming.ContainsKey(k)).ToList())
        {
            var vm = _byKey[gone];
            vm.PropertyChanged -= OnItemChanged;
            Items.Remove(vm);
            _byKey.Remove(gone);
            if (Selected == vm) Selected = null;
        }
        foreach (var (key, app) in incoming)
        {
            if (_byKey.TryGetValue(key, out var vm))
            {
                vm.Update(app);
                if (vm.IsChecked && !vm.CanSelect) vm.IsChecked = false;
            }
            else
            {
                vm = new AppItemViewModel(app);
                vm.PropertyChanged += OnItemChanged;
                Items.Add(vm);
                _byKey[key] = vm;
            }
        }

        CheckedCount = Items.Count(i => i.IsChecked);
        var games = snap.Apps.Count(a => a.Channel == UpdateChannel.Game);
        var store = snap.Apps.Count(a => a.Source == AppSource.Msix);
        Summary = $"{snap.Apps.Count} apps · {snap.Apps.Count - store} programs · {store} Store apps · {games} games · scanned {Format.Ago(snap.TakenAt)}";
        PerfLog.Duration($"apps list merge ({Items.Count} rows)", sw.Elapsed);
    }

    private void OnSizeMeasured(AppEntry app)
    {
        if (_byKey.TryGetValue(app.Key, out var vm)) vm.NotifySizeChanged();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppItemViewModel.IsChecked))
        {
            CheckedCount = Items.Count(i => i.IsChecked);
            OnPropertyChanged(nameof(CheckedSummary));
        }
    }

    private bool Filter(object o)
    {
        var a = ((AppItemViewModel)o).Entry;
        if (UpdatesOnly && !(a.HasUpdate && a.Channel == UpdateChannel.Winget)) return false;
        if (ScopeIndex == 1 && a.Scope != InstallScope.User) return false;
        if (ScopeIndex == 2 && a.Scope != InstallScope.Machine) return false;

        switch (KindIndex)
        {
            case 1 when a.Source != AppSource.Arp || a.Channel is UpdateChannel.Game or UpdateChannel.Driver or UpdateChannel.System: return false;
            case 2 when a.Source != AppSource.Msix: return false;
            case 3 when a.Channel != UpdateChannel.Game: return false;
            case 4 when a.Channel is not (UpdateChannel.Driver or UpdateChannel.System): return false;
        }

        var q = SearchText.Trim();
        if (q.Length == 0) return true;
        return a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (a.Publisher?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (a.WingetId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    [RelayCommand]
    private Task Refresh() => _store.RefreshAsync();

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var i in Items) i.IsChecked = false;
    }

    // ---------- bulk uninstall ----------

    [RelayCommand(CanExecute = nameof(HasChecked))]
    private Task UninstallSelected() => OpenUninstallAsync(Items.Where(i => i.IsChecked && i.CanSelect).Select(i => i.Entry).ToList());

    private async Task OpenUninstallAsync(List<AppEntry> selected)
    {
        if (selected.Count == 0 || _store.Current is null) return;
        var vm = new UninstallViewModel(selected, _store.Current.Apps, _helper, _db);
        if (Platform.ShowUninstall(vm))
        {
            foreach (var i in Items) i.IsChecked = false;
            await _store.RefreshAsync();
        }
    }

    // ---------- per-app actions (right-click menu + details panel) ----------

    [RelayCommand]
    private void ShowDetails(AppItemViewModel? item)
    {
        if (item is null) return;
        Selected = item;
        IsDetailsOpen = true;
    }

    [RelayCommand]
    private void CloseDetails() => IsDetailsOpen = false;

    [RelayCommand]
    private async Task UninstallOne(AppItemViewModel? item)
    {
        item ??= Selected;
        if (item is null) return;
        if (!item.CanSelect)
        {
            item.ActionStatus = item.IsProtected ? "Protected — choose Unprotect first." : "This app doesn't offer an uninstaller.";
            return;
        }
        await OpenUninstallAsync([item.Entry]);
    }

    [RelayCommand]
    private async Task UpdateNow(AppItemViewModel? item)
    {
        item ??= Selected;
        if (item is null || item.IsBusy) return;
        if (!item.HasUpdate || item.Entry.WingetId is not { } id)
        {
            item.ActionStatus = "No update available through winget.";
            return;
        }

        var app = item.Entry;
        var running = RunningApps.InFolder(app.InstallLocation);
        if (running.Count > 0)
        {
            if (!await Dialogs.ConfirmAsync($"{app.Name} is running. Close it and update? (It's asked to close normally, so unsaved-work prompts still appear.)", "Close and update")) return;
            if (!await RunningApps.CloseGracefullyAsync(running, TimeSpan.FromSeconds(15)))
            {
                item.ActionStatus = "It didn't close — update skipped.";
                return;
            }
        }

        var candidate = new UpdateCandidate
        {
            Key = $"winget:{id}", Source = UpdateSource.Winget, Title = app.Name,
            CurrentVersion = app.Version, AvailableVersion = app.AvailableVersion,
            NeedsAdmin = app.Scope == InstallScope.Machine, App = app, PackageId = id,
        };
        item.IsBusy = true;
        item.ActionStatus = candidate.NeedsAdmin ? "Updating (admin)…" : "Updating…";
        var output = new List<string>();
        UpdateRunResult r;
        try
        {
            r = await new UpdateExecutor(_helper).RunAsync(candidate, l => Platform.OnUiThread(() => { output.Add(l); item.ActionStatus = l; }), CancellationToken.None);
        }
        catch (HelperCancelledException)
        {
            r = new UpdateRunResult(OpStatus.Cancelled, "Admin approval was cancelled.", true);
        }
        catch (Exception ex)
        {
            r = new UpdateRunResult(OpStatus.Failed, ex.Message, candidate.NeedsAdmin);
        }
        item.IsBusy = false;
        item.ActionStatus = r.Warnings is { Count: > 0 } w ? $"{r.Message} — {string.Join(" ", w)}" : r.Message;

        _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "update", candidate.Key, app.Name, "Winget", app.Version, app.AvailableVersion,
            r.Elevated, r.Status.ToString(), r.ExitCode, item.ActionStatus, string.Join(Environment.NewLine, output)));
        if (r.Ok)
        {
            _updates.MarkDone([candidate]);
            await _store.RefreshAsync();
        }
        LoadLastAction(item);
    }

    [RelayCommand]
    private async Task ToggleProtectAsync(AppItemViewModel? item)
    {
        item ??= Selected;
        if (item is null) return;
        if (ProtectedApps.IsAlwaysProtected(item.Entry))
        {
            await Dialogs.InfoAsync("Salad is permanently protected. It cannot be unprotected, closed, updated or uninstalled by C.A.R.S.");
            return;
        }
        var list = _settings.ProtectedApps.ToList();
        if (item.IsProtected)
        {
            var matches = list.Where(p => item.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)).ToList();
            var broad = matches.Where(p => !p.Equals(item.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (broad.Count > 0 &&
                !await Dialogs.ConfirmAsync($"\"{item.Name}\" is protected by the rule(s): {string.Join(", ", broad.Select(b => $"\"{b}\""))}.\n\nRemoving them unprotects every app whose name starts that way.", "Unprotect them"))
                return;
            list.RemoveAll(p => matches.Contains(p));
        }
        else
        {
            list.Add(item.Name);
        }
        _settings.ProtectedApps = list;
        _store.Protected = new ProtectedApps(list);
        foreach (var vm in Items)
        {
            vm.Entry.IsProtected = _store.Protected.IsProtected(vm.Entry);
            vm.Update(vm.Entry);
        }
        item.ActionStatus = item.IsProtected ? "Protected — it can't be ticked or uninstalled until you unprotect it. Updates still work." : "No longer protected — it can be uninstalled again.";
    }

    [RelayCommand]
    private static void OpenLocation(AppItemViewModel? item)
    {
        var loc = item?.Entry.InstallLocation;
        if (loc is not null && Directory.Exists(loc))
            Platform.ShowInFileManager(loc);
        else if (item is not null)
            item.ActionStatus = "This app didn't record an install folder.";
    }

    [RelayCommand]
    private static void CopyName(AppItemViewModel? item)
    {
        if (item is not null) Platform.CopyText(item.Name);
    }

    [RelayCommand]
    private static void CopyWingetId(AppItemViewModel? item)
    {
        if (item?.Entry.WingetId is { } id) Platform.CopyText(id);
        else if (item is not null) item.ActionStatus = "Not a winget package.";
    }

    private void LoadLastAction(AppItemViewModel? item)
    {
        if (item is null) return;
        var keys = new List<string> { item.Entry.Key };
        if (item.Entry.WingetId is { } id) keys.Add($"winget:{id}");
        var last = _db.ListHistory(1, targetKeys: keys).FirstOrDefault();
        item.LastAction = last is null ? "No C.A.R.S. actions yet" : $"{last.Kind} · {last.Status} · {Format.Ago(last.Started)}";
    }
}
