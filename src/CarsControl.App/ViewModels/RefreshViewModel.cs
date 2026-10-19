using System.Collections.ObjectModel;
using System.Windows;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Updates;
using CarsControl.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

/// <summary>What a banner means, so it gets the right color (your accent is not "info").</summary>
public enum NoticeKind { Info, Success, Warning, Error }

public sealed partial class UpdateItemViewModel(UpdateCandidate candidate) : ObservableObject
{
    public UpdateCandidate Candidate { get; } = candidate;
    public string Title => Candidate.Title;
    public string VersionText => Candidate.VersionText;
    public bool NeedsAdmin => Candidate.NeedsAdmin;
    public string? Note => Candidate.Note;
    public bool HasNote => !string.IsNullOrEmpty(Candidate.Note);
    public bool CanIgnore => !Candidate.IsCheckAction;

    /// <summary>Set when this version can't succeed (winget's catalog is broken for it): the row offers Skip this version.</summary>
    [ObservableProperty] private bool _offerSkip;

    // ---- icon: the app's own for winget updates, a symbol for the other sources ----
    private System.Windows.Media.ImageSource? _icon;
    private bool _iconRequested;

    public System.Windows.Media.ImageSource? Icon
    {
        get
        {
            if (!_iconRequested && Candidate.App is { } app && AppItemViewModel.IconProvider is { } load)
            {
                _iconRequested = true;
                _ = LoadIconAsync(load, app);
            }
            return _icon;
        }
    }

    public bool HasIcon => _icon is not null;

    private async Task LoadIconAsync(Func<CarsControl.Core.Models.AppEntry, Task<System.Windows.Media.ImageSource?>> load, CarsControl.Core.Models.AppEntry app)
    {
        var img = await load(app);
        if (img is null) return;
        _icon = img;
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
    }

    public Wpf.Ui.Controls.SymbolRegular SourceSymbol => Candidate.Source switch
    {
        UpdateSource.WindowsUpdate => Wpf.Ui.Controls.SymbolRegular.Window24,
        UpdateSource.Store => Wpf.Ui.Controls.SymbolRegular.ShoppingBag24,
        UpdateSource.Office => Wpf.Ui.Controls.SymbolRegular.Document24,
        UpdateSource.Wsl => Wpf.Ui.Controls.SymbolRegular.WindowConsole20,
        UpdateSource.Npm or UpdateSource.Pip or UpdateSource.PsModule => Wpf.Ui.Controls.SymbolRegular.Code24,
        _ => Wpf.Ui.Controls.SymbolRegular.Apps24,
    };

    /// <summary>Winget updates can show what changed: opens the release notes page (or the homepage).</summary>
    public bool CanShowChanges => Candidate.Source == UpdateSource.Winget && Candidate.PackageId is not null;

    public override string ToString() => $"{Title} {VersionText}";

    [ObservableProperty] private bool _isSelected = candidate.SelectedByDefault;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(IsSucceeded))]
    private ItemState _state;

    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private string? _lastLine;
    [ObservableProperty] private string? _warning;

    public bool IsRunning => State == ItemState.Running;
    public bool IsDone => State is ItemState.Succeeded or ItemState.Failed or ItemState.Skipped;
    public bool IsFailed => State == ItemState.Failed;
    public bool IsSucceeded => State == ItemState.Succeeded;
    public List<string> Output { get; } = [];
}

public sealed partial class UpdateGroupViewModel(UpdateSource source, string title) : ObservableObject
{
    public UpdateSource Source { get; } = source;
    public string Title { get; } = title;
    public ObservableCollection<UpdateItemViewModel> Items { get; } = [];

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string _status = "Not checked yet";
    [ObservableProperty] private bool _hasError;
}

/// <summary>Shows UpdateCenter's results (the same numbers Home, the tray and toasts use) and runs
/// updates. Item view models are kept by key across refreshes so selections and statuses survive.</summary>
public sealed partial class RefreshViewModel : ObservableObject
{
    private readonly InventoryStore _inventory;
    private readonly CarsDb _db;
    private readonly UpdateCenter _center;
    private readonly HelperClient _helper;
    private readonly UpdateRunner _runner;
    private readonly Dictionary<string, UpdateItemViewModel> _vms = new();

    public ObservableCollection<UpdateGroupViewModel> Groups { get; } = [];

    /// <summary>Microsoft Store and Microsoft 365: "go check" actions, listed apart and never counted as found updates.</summary>
    public ObservableCollection<UpdateItemViewModel> CheckActions { get; } = [];

    public ObservableCollection<IgnoredItem> Ignored { get; } = [];

    /// <summary>Optional Windows Update items: collapsed, unticked, not counted.</summary>
    public ObservableCollection<UpdateItemViewModel> Optional { get; } = [];

    public string OptionalHeader => $"Optional Windows updates ({Optional.Count})";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(UpdateSelectedCommand))]
    private bool _isChecking;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(UpdateSelectedCommand), nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _summary = "Not checked yet";
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private bool _adminSessionActive;
    [ObservableProperty] private bool _dryRun;
    [ObservableProperty] private string? _importOffer;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;

    /// <summary>After a run: the banner offers a jump to History, where finished updates now live.</summary>
    [ObservableProperty] private bool _bannerLinksHistory;
    [ObservableProperty] private bool _nothingFound;

    public string IgnoredHeader => $"Ignored ({Ignored.Count})";

    public RefreshViewModel(InventoryStore inventory, HelperClient helper, CarsDb db, UpdateCenter center, UpdateRunner runner)
    {
        _inventory = inventory;
        _db = db;
        _center = center;
        _helper = helper;
        _runner = runner;
        _helper.ConnectionChanged += active => Platform.OnUiThread(() =>
        {
            AdminSessionActive = active;
            UpdateSelection();
        });
        Ignored.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IgnoredHeader));
        Optional.CollectionChanged += (_, _) => OnPropertyChanged(nameof(OptionalHeader));

        foreach (var s in UpdateCenter.Order.Where(s => s is not (UpdateSource.Store or UpdateSource.Office)))
            Groups.Add(new UpdateGroupViewModel(s, UpdateCenter.SourceTitle(s)));
        foreach (var c in center.CheckActions) CheckActions.Add(Vm(c));
        foreach (var vm in CheckActions) vm.PropertyChanged += OnItemPropertyChanged;

        if (!App.IsDemo && db.GetSetting(UniGetUiImporter.ImportedSettingKey) is null && UniGetUiImporter.Preview() is { } p)
            ImportOffer = $"Import {p.IgnoreRules} ignore rule(s) and {p.HistoryEntries} history entries from UniGetUI?";

        // Any run shows here, whoever started it (this page, Home, maintenance, the palette).
        _runner.RunningChanged += running => IsRunning = running;
        _runner.ProgressChanged += (pct, text) =>
        {
            Progress = pct;
            ProgressText = text ?? (IsRunning ? ProgressText : null);
        };
        _runner.ItemStarted += c => Start(Vm(c));
        _runner.ItemLine += (c, text) =>
        {
            if (!_vms.TryGetValue(Id(c), out var vm)) return;
            vm.Output.Add(text);
            vm.LastLine = text;
        };
        _runner.ItemFinished += o =>
        {
            var vm = Vm(o.Candidate);
            vm.State = o.State;
            vm.StatusText = o.StatusText;
            vm.Warning = o.Warning;
            vm.OfferSkip = o.Result.Mismatch is not null;
            vm.LastLine = null;
        };
        _runner.RunFinished += OnRunFinished;

        _center.Changed += Rebuild;
        Rebuild();
    }

    private static string Id(UpdateCandidate c) => $"{c.Key}@{c.AvailableVersion}";

    private UpdateItemViewModel Vm(UpdateCandidate c)
    {
        var id = Id(c);
        if (_vms.TryGetValue(id, out var existing)) return existing;
        var vm = new UpdateItemViewModel(c);
        vm.PropertyChanged += OnItemPropertyChanged;
        return _vms[id] = vm;
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateItemViewModel.IsSelected)) UpdateSelection();
    }

    /// <summary>Re-syncs groups with UpdateCenter. Skipped while updates run so rows don't vanish
    /// mid-run; the end of every run rebuilds.</summary>
    private void Rebuild()
    {
        IsChecking = _center.IsChecking;
        if (IsRunning) return;

        foreach (var g in Groups)
        {
            var st = _center.State(g.Source);
            var items = _center.ItemsFor(g.Source).Select(Vm).ToList();
            if (!items.SequenceEqual(g.Items))
            {
                g.Items.Clear();
                foreach (var i in items) g.Items.Add(i);
            }
            g.IsChecking = st.IsChecking;
            g.HasError = st.Error is not null;
            g.Status = st.IsChecking ? "Checking…"
                : st.Error is { } err ? $"Couldn't check: {err}"
                : st.CheckedAt is null ? "Not checked yet"
                : (items.Count == 0 ? "Up to date" : $"{items.Count} available") + $" · checked {Format.Ago(st.CheckedAt.Value)}";
        }

        var optional = _center.Optional.Select(Vm).ToList();
        if (!optional.SequenceEqual(Optional))
        {
            Optional.Clear();
            foreach (var o in optional) Optional.Add(o);
        }

        Ignored.Clear();
        foreach (var i in _center.Ignored.OrderBy(i => i.Title)) Ignored.Add(i);
        UpdateSelection();
    }

    private IEnumerable<UpdateItemViewModel> AllItems => Groups.SelectMany(g => g.Items).Concat(Optional).Concat(CheckActions);

    private void UpdateSelection()
    {
        var selected = AllItems.Where(i => i.IsSelected && i.State is ItemState.Pending or ItemState.Failed).ToList();
        SelectedCount = selected.Count;
        var admin = selected.Count(i => i.NeedsAdmin);
        SelectionText = selected.Count == 0 ? "Nothing selected"
            : $"{selected.Count} selected" + (admin > 0 ? (AdminSessionActive ? $" · {admin} need admin (session active)" : $" · {admin} need admin — you'll be asked once") : " · no admin needed");

        var found = _center.Found.Count;
        NothingFound = found == 0 && !_center.IsChecking && _center.LastChecked is not null;
        Summary = _center.IsChecking ? "Checking…"
            : _center.LastChecked is { } t ? $"{found} update{(found == 1 ? "" : "s")} found · checked {Format.Ago(t)}" + (Ignored.Count > 0 ? $" · {Ignored.Count} ignored" : "")
            : "Not checked yet";
        UpdateSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Page opened: show saved results right away; refresh only the sources that are stale.</summary>
    public void EnsureFresh()
    {
        if (!_center.IsChecking) _ = _center.CheckAsync(force: false);
    }

    private bool CanCheck() => !IsChecking && !IsRunning;

    /// <summary>Check now starts over: finished and failed rows are forgotten (anything still outdated
    /// comes back fresh), and the always-checked rows are ticked again.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        Banner = null;
        BannerLinksHistory = false;
        foreach (var id in _vms.Where(kv => kv.Value.State != ItemState.Pending).Select(kv => kv.Key).ToList()) _vms.Remove(id);
        foreach (var a in CheckActions)
        {
            a.State = ItemState.Pending;
            a.StatusText = null;
            a.Warning = null;
            a.IsSelected = a.Candidate.SelectedByDefault;
        }
        await _center.CheckAsync(force: true);
    }

    [RelayCommand]
    private static void OpenHistory() => Navigator.Go("history");

    /// <summary>Home's "Update everything": refresh stale sources, then update everything ticked by default.</summary>
    public async Task UpdateEverythingAsync()
    {
        if (IsRunning) return;
        await _center.CheckAsync(force: false);
        Rebuild();
        if (CanUpdate()) await UpdateSelectedAsync();
        else
        {
            Banner = "Everything is up to date.";
            BannerKind = NoticeKind.Success;
            BannerLinksHistory = false;
        }
    }

    private bool CanUpdate() => !IsRunning && !IsChecking && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateSelectedAsync()
    {
        var items = AllItems.Where(i => i.IsSelected && i.State is ItemState.Pending or ItemState.Failed).Select(i => i.Candidate).ToList();
        if (items.Count == 0) return;
        Banner = null;
        BannerLinksHistory = false;
        await _runner.RunAsync(items, new UpdateRunOptions { Origin = "Refresh", DryRun = DryRun, AskAboutOpenApps = AskAboutOpenAppsAsync });
    }

    private void OnRunFinished(UpdateRunSummary s)
    {
        var text = $"{s.Updated} updated" + (s.Ran > 0 ? $", {s.Ran} asked to check for their own updates" : "");
        var from = s.Origin == "Refresh" ? "" : $"{s.Origin}: ";
        var waiting = s.Waiting.Count > 0 ? $" {s.Waiting.Count} need admin and wait for your approval (Home)." : "";
        if (s.Cancelled)
        {
            Banner = "Stopped. Items already finished stay updated.";
            BannerKind = NoticeKind.Warning;
        }
        else if (s.DryRun)
        {
            Banner = "Dry run finished — nothing was changed. Each row shows the exact command it would run.";
            BannerKind = NoticeKind.Info;
        }
        else
        {
            Banner = s.Failed == 0
                ? $"{from}{text}{(s.RestartNeeded ? " — restart to finish some of them" : "")}. Finished updates are in History.{waiting}"
                : $"{from}{text}, {s.Failed} failed — they stay below with the reason. Everything is in History.{waiting}";
            BannerKind = s.Failed == 0 ? NoticeKind.Success : NoticeKind.Warning;
        }
        BannerLinksHistory = !s.DryRun;

        if (!s.DryRun)
        {
            // Done means gone: finished updates leave the list (they're in History) and their rows are
            // forgotten, so if a later check finds one again it shows up fresh, not with an old green tick.
            foreach (var o in s.Outcomes.Where(o => o.State == ItemState.Succeeded && !o.Candidate.IsCheckAction)) _vms.Remove(Id(o.Candidate));
            // Store and Microsoft 365 are actions, not updates: they stay, unticked, saying when they ran.
            foreach (var o in s.Outcomes.Where(o => o.State == ItemState.Succeeded && o.Candidate.IsCheckAction))
            {
                var a = Vm(o.Candidate);
                a.IsSelected = false;
                a.StatusText = $"Ran at {DateTime.Now:HH:mm} — {a.StatusText}";
            }
        }
        Rebuild();
    }

    private static void Start(UpdateItemViewModel item)
    {
        item.State = ItemState.Running;
        item.StatusText = item.NeedsAdmin ? "Running as admin…" : "Running…";
        item.Warning = null;
        item.Output.Clear();
    }

    private static Task<OpenAppsChoice> AskAboutOpenAppsAsync(IReadOnlyList<UpdateCandidate> open) =>
        Dialogs.AskAboutOpenAppsAsync(open.Select(c => c.Title));

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _runner.Cancel();

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in AllItems.Where(i => i.State is ItemState.Pending or ItemState.Failed)) i.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in AllItems) i.IsSelected = false;
    }

    [RelayCommand]
    private async Task ShowChangesAsync(UpdateItemViewModel? item)
    {
        if (item?.Candidate.PackageId is not { } id) return;
        item.Warning = null;
        item.LastLine = "Looking up the release notes…";
        var info = await CarsControl.Core.Winget.WingetShow.ShowAsync(id);
        item.LastLine = null;
        if (info?.BestUrl is { } url) Platform.OpenUri(url);
        else item.Warning = "This package doesn't list release notes or a homepage.";
    }

    [RelayCommand]
    private void SkipVersion(UpdateItemViewModel? item)
    {
        if (item is null || !item.CanIgnore) return;
        _db.SetIgnore(item.Candidate.Key, item.Candidate.AvailableVersion);
        _center.IgnoreRulesChanged();
    }

    [RelayCommand]
    private void IgnorePermanently(UpdateItemViewModel? item)
    {
        if (item is null || !item.CanIgnore) return;
        _db.SetIgnore(item.Candidate.Key, null);
        _center.IgnoreRulesChanged();
    }

    [RelayCommand]
    private void StopIgnoring(IgnoredItem? item)
    {
        if (item is null) return;
        _db.RemoveIgnore(item.Key);
        _center.IgnoreRulesChanged();
    }

    [RelayCommand]
    private void ImportUniGetUi()
    {
        var (rules, history) = UniGetUiImporter.Import(_db);
        ImportOffer = null;
        Banner = $"Imported {rules} ignore rule(s) and {history} history entries from UniGetUI.";
        _center.IgnoreRulesChanged();
    }

    [RelayCommand]
    private void DismissImport()
    {
        _db.SetSetting(UniGetUiImporter.ImportedSettingKey, "declined");
        ImportOffer = null;
    }
}
