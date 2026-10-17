using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Health;
using CarsControl.Core.Tweaks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CarsControl.App.ViewModels;

public sealed partial class TweakRowViewModel(Tweak tweak, PrivacyViewModel owner) : ObservableObject
{
    public Tweak Tweak { get; } = tweak;
    public string Title => Tweak.Title;
    public string Description => Tweak.Description;
    public string? Note => Tweak.Note;
    public string? Caution { get; set; }
    public bool HasCaution => Caution is not null;
    public bool NeedsAdmin => Tweak.NeedsAdmin;
    public string ScopeText => Tweak.Scope == TweakScope.Machine ? "All users" : "Just you";
    public string ScopeTip => Tweak.Scope == TweakScope.Machine
        ? "Changes it for every account on this PC, so it goes through the admin helper (one approval)."
        : "Changes it only for your account. No admin approval needed.";
    public CarsControl.Core.Help.Recommendation? Recommendation => CarsControl.Core.Help.Recommendations.ForPrivacy(Tweak.Id, Title, Tweak.Group == TweakGroup.Taskbar, HasCaution);
    public string Details => TweakEngine.Describe(Tweak);

    /// <summary>Accessible name for the switch ("No advertising ID").</summary>
    public override string ToString() => Title;

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _isPartly;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private string? _status;

    internal bool Syncing { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (!Syncing) _ = owner.ChangeAsync(this, value);
    }

    [RelayCommand]
    private Task DefaultAsync() => owner.ResetToDefaultAsync(this);
}

public sealed partial class TweakGroupViewModel(TweakGroup group) : ObservableObject
{
    public TweakGroup Group { get; } = group;
    public string Title => TweakCatalog.GroupTitle(Group);
    public string Description => Group switch
    {
        TweakGroup.Telemetry => "Review optional usage reporting; Windows security and error reporting remain enabled.",
        TweakGroup.Ads => "Reduce advertising IDs, promotional tips and personalized suggestions.",
        TweakGroup.AI => "Choose which optional AI experiences Windows may offer on this PC.",
        TweakGroup.Search => "Choose whether search and widgets include web content and news.",
        TweakGroup.Edge => "Review optional browser recommendations and diagnostic preferences.",
        TweakGroup.Office => "Review optional Office connected experiences and usage preferences.",
        _ => "Choose which taskbar controls are visible; these choices are a matter of preference."
    };
    public ObservableCollection<TweakRowViewModel> Rows { get; } = [];
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _allOn;
}

public sealed partial class AddOnRowViewModel(StoreAddOn addOn) : ObservableObject
{
    public StoreAddOn AddOn { get; } = addOn;
    public string Title => AddOn.Title;
    public string Why => AddOn.Why;
    public override string ToString() => Title;
    [ObservableProperty] private string? _fullName;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;
    public bool IsInstalled => FullName is not null;
    partial void OnFullNameChanged(string? value) => OnPropertyChanged(nameof(IsInstalled));
}

public sealed record CapabilityRow(string App, string Capability, string When, bool InUse, string? Path)
{
    public override string ToString() => $"{App}, {Capability}, {When}";
}

/// <summary>
/// The Privacy page: switches from <see cref="TweakCatalog"/> with their live state, removable Store
/// add-ons and the camera/mic/location history. Your-account switches are changed here, as you, with a
/// snapshot kept in cars.db; admin ones go through the helper, which keeps its own snapshot.
/// </summary>
public sealed partial class PrivacyViewModel : ObservableObject
{
    private const string SnapshotKey = "tweak_snapshots_v1";
    private readonly HelperClient _helper;
    private readonly CarsDb _db;
    private readonly InventoryStore _inventory;
    private readonly AppSettings _settings;
    private readonly string? _sid = WindowsIdentity.GetCurrent().User?.Value;
    private Dictionary<string, List<SavedValue>> _snapshots = new();

    public ObservableCollection<TweakGroupViewModel> Groups { get; } = [];
    public ObservableCollection<AddOnRowViewModel> AddOns { get; } = [];

    /// <summary>Installed ones get a row; the rest are one line of "get it back" links.</summary>
    public ObservableCollection<AddOnRowViewModel> InstalledAddOns { get; } = [];
    public ObservableCollection<AddOnRowViewModel> RemovedAddOns { get; } = [];
    public ObservableCollection<CapabilityRow> Accesses { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;
    [ObservableProperty] private string _addOnSummary = "";
    [ObservableProperty] private bool _hasAccesses;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyAllRecommendedCommand))]
    private bool _isBusy;

    public PrivacyViewModel(HelperClient helper, CarsDb db, InventoryStore inventory, AppSettings settings)
    {
        _helper = helper;
        _db = db;
        _inventory = inventory;
        _settings = settings;
        LoadSnapshots();
        var build = Environment.OSVersion.Version.Build;
        foreach (var g in TweakCatalog.Available(build).GroupBy(t => t.Group).OrderBy(g => g.Key))
        {
            var group = new TweakGroupViewModel(g.Key);
            foreach (var t in g) group.Rows.Add(new TweakRowViewModel(t, this));
            Groups.Add(group);
        }
        foreach (var a in StoreAddOns.All) AddOns.Add(new AddOnRowViewModel(a));
    }

    private IEnumerable<TweakRowViewModel> AllRows => Groups.SelectMany(g => g.Rows);

    /// <summary>Re-reads everything live (the page calls it when shown).</summary>
    [RelayCommand]
    public void Refresh()
    {
        foreach (var row in AllRows) ReadRow(row);
        UpdateSummaries();
        _ = RefreshAddOnsAsync();
        _ = RefreshAccessesAsync();
    }

    private void ReadRow(TweakRowViewModel row)
    {
        var t = row.Tweak;
        var state = TweakEngine.Read(t, t.Scope == TweakScope.Machine ? Registry.LocalMachine : Registry.CurrentUser);
        row.Syncing = true;
        row.IsOn = state == TweakState.On;
        row.Syncing = false;
        row.IsPartly = state == TweakState.Partly;
        row.CanUndo = t.Scope == TweakScope.User ? _snapshots.ContainsKey(t.Id) : TweakSnapshots.HelperHasUndo(t, _sid);
        row.Caution = t.Caution?.Invoke();
        if (row.IsPartly && row.Status is null) row.Status = "Partly on (something else changed some of its settings)";
    }

    private void UpdateSummaries()
    {
        foreach (var g in Groups)
        {
            var on = g.Rows.Count(r => r.IsOn);
            g.Summary = $"{on} of {g.Rows.Count} on";
            g.AllOn = g.Rows.All(r => r.IsOn || r.HasCaution);
        }
        var rows = AllRows.ToList();
        var admin = rows.Count(r => IsPrivacy(r) && !r.IsOn && r.NeedsAdmin && !r.HasCaution);
        Summary = $"{rows.Count(r => r.IsOn)} of {rows.Count} switches on" + (admin > 0 ? $" · {admin} of the rest need admin (one prompt)" : "");
        ApplyAllRecommendedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A switch was flipped: on = apply, off = undo (back to exactly what was there before, or
    /// Windows' default when C.A.R.S. never changed it).</summary>
    public async Task ChangeAsync(TweakRowViewModel row, bool on)
    {
        if (row.IsBusy) return;
        row.IsBusy = true;
        row.Status = null;
        try
        {
            var ok = await SetAsync(row.Tweak, on ? "apply" : "undo");
            if (!ok.Ok) row.Status = ok.Message;
            else Record(row.Tweak, on ? "Turned on" : "Undone");
        }
        finally
        {
            row.IsBusy = false;
            ReadRow(row);
            UpdateSummaries();
        }
    }

    public async Task ResetToDefaultAsync(TweakRowViewModel row)
    {
        if (row.IsBusy) return;
        row.IsBusy = true;
        try
        {
            var ok = await SetAsync(row.Tweak, "default");
            row.Status = ok.Ok ? "Back to the Windows default." : ok.Message;
            if (ok.Ok) Record(row.Tweak, "Set to the Windows default");
        }
        finally
        {
            row.IsBusy = false;
            ReadRow(row);
            UpdateSummaries();
        }
    }

    /// <summary>Taskbar buttons are layout preferences, not privacy: "Turn all on" never touches them
    /// (the group still has its own "Turn this group on").</summary>
    private static bool IsPrivacy(TweakRowViewModel r) => r.Tweak.Group != TweakGroup.Taskbar;

    /// <summary>How many switches that change only your own account (no admin) are off and safe to turn on.</summary>
    public int UserSwitchesOff => AllRows.Count(r => IsPrivacy(r) && r.Tweak.Scope == TweakScope.User && !r.IsOn && !r.HasCaution);

    /// <summary>The first-time setup's "turn on the ones that need no admin".</summary>
    public Task ApplyUserSwitchesAsync() =>
        ApplyRowsAsync(AllRows.Where(r => IsPrivacy(r) && r.Tweak.Scope == TweakScope.User && !r.IsOn && !r.HasCaution).ToList());

    private bool CanApplyAll() => !IsBusy && AllRows.Any(r => IsPrivacy(r) && !r.IsOn && !r.HasCaution);

    /// <summary>Every privacy switch that's off and has no caution on this PC. One admin prompt at most.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyAll))]
    private async Task ApplyAllRecommendedAsync() => await ApplyRowsAsync(AllRows.Where(r => IsPrivacy(r) && !r.IsOn && !r.HasCaution).ToList());

    [RelayCommand]
    private async Task ApplyGroupAsync(TweakGroupViewModel? group)
    {
        if (group is null) return;
        await ApplyRowsAsync(group.Rows.Where(r => !r.IsOn && !r.HasCaution).ToList());
    }

    private async Task ApplyRowsAsync(List<TweakRowViewModel> rows)
    {
        if (rows.Count == 0 || IsBusy) return;
        IsBusy = true;
        Banner = null;
        var done = 0;
        var cancelled = false;
        var restoreNote = "";
        try
        {
            // The machine-wide ones need the admin helper anyway: take a restore point in the same session, when you want one.
            if (rows.Any(r => r.NeedsAdmin))
            {
                var rp = await SafetyNet.MakeAsync(_helper, _settings.Store.Current.Health, SafetyNet.Read(), RiskyChange.PrivacyBatch, $"{rows.Count} switches", HelperClient.DevUnelevated);
                cancelled = rp.Outcome == RestoreOutcome.Cancelled;
                if (rp.Outcome == RestoreOutcome.Failed) restoreNote = " No restore point could be made, but each switch can still be undone.";
            }

            // Your-account switches first (no prompt), then the admin ones in one helper session.
            foreach (var row in rows.OrderBy(r => r.NeedsAdmin))
            {
                if (cancelled && row.NeedsAdmin)
                {
                    row.Status = "Skipped — admin approval was cancelled";
                    continue;
                }
                row.IsBusy = true;
                var r = await SetAsync(row.Tweak, "apply");
                row.IsBusy = false;
                if (r.Ok)
                {
                    done++;
                    Record(row.Tweak, "Turned on");
                }
                else
                {
                    row.Status = r.Message;
                    if (r.Message.Contains("cancelled")) cancelled = true;
                }
                ReadRow(row);
            }
            Banner = cancelled ? $"Turned on {done}. The admin ones were skipped because the prompt was cancelled."
                : $"Turned on {done} switch{(done == 1 ? "" : "es")}. Each one can be undone by switching it off." + restoreNote;
            BannerKind = cancelled ? NoticeKind.Warning : NoticeKind.Success;
        }
        finally
        {
            IsBusy = false;
            UpdateSummaries();
        }
    }

    private async Task<(bool Ok, string Message)> SetAsync(Tweak t, string action)
    {
        if (t.Scope == TweakScope.User)
        {
            try
            {
                var root = Registry.CurrentUser;
                switch (action)
                {
                    case "apply":
                        if (!_snapshots.ContainsKey(t.Id)) _snapshots[t.Id] = TweakEngine.Snapshot(t, root).ToList();
                        TweakEngine.Apply(t, root);
                        break;
                    case "undo" when _snapshots.TryGetValue(t.Id, out var saved):
                        TweakEngine.Restore(t, saved, root);
                        _snapshots.Remove(t.Id);
                        break;
                    default:
                        TweakEngine.SetDefaults(t, root);
                        _snapshots.Remove(t.Id);
                        break;
                }
                SaveSnapshots();
                if (t.Group is TweakGroup.Taskbar or TweakGroup.AI or TweakGroup.Ads) BroadcastSettingChange();
                return (true, "Done.");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                return (false, $"Windows refused the change: {ex.Message}");
            }
        }

        try
        {
            var r = await _helper.SendAsync(new TweakOp(t.Id, action), dryRun: HelperClient.DevUnelevated);
            return r.Ok ? (true, r.Message ?? "Done.") : (false, r.Message ?? r.Status.ToString());
        }
        catch (HelperCancelledException)
        {
            return (false, "Admin approval was cancelled, so nothing changed.");
        }
        catch (HelperUnavailableException ex)
        {
            return (false, ex.Message);
        }
    }

    private void Record(Tweak t, string what) =>
        _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "privacy", $"tweak:{t.Id}", t.Title, "Privacy", null, null,
            t.NeedsAdmin, "Succeeded", 0, what, TweakEngine.Describe(t)));

    // ---------- Store add-ons ----------

    private async Task RefreshAddOnsAsync()
    {
        var installed = await Task.Run(StoreAddOnManager.Installed);
        foreach (var a in AddOns) a.FullName = installed.GetValueOrDefault(a.AddOn.PackageName);
        InstalledAddOns.Clear();
        RemovedAddOns.Clear();
        foreach (var a in AddOns) (a.IsInstalled ? InstalledAddOns : RemovedAddOns).Add(a);
        var count = InstalledAddOns.Count;
        AddOnSummary = count == 0 ? "None of these are installed for you." : $"{count} of {AddOns.Count} installed for you";
    }

    [RelayCommand]
    private async Task RemoveAddOnAsync(AddOnRowViewModel? row)
    {
        if (row?.FullName is not { } full || row.IsBusy) return;
        if (!await Dialogs.ConfirmAsync($"Remove {row.Title} for your account? You can get it back from the Microsoft Store any time.", "Remove", danger: true)) return;
        row.IsBusy = true;
        var (ok, message) = await StoreAddOnManager.RemoveAsync(full);
        row.IsBusy = false;
        row.Status = ok ? "Removed." : $"Couldn't remove it: {message}";
        if (ok)
            _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "privacy", $"addon:{row.AddOn.PackageName}", row.Title, "Privacy", null, null,
                false, "Succeeded", 0, "Removed the Store app for your account", null));
        await RefreshAddOnsAsync();
    }

    [RelayCommand]
    private static void ReinstallAddOn(AddOnRowViewModel? row)
    {
        if (row is not null) Platform.OpenUri(row.AddOn.StoreLink);
    }

    // ---------- camera, mic, location ----------

    private async Task RefreshAccessesAsync()
    {
        var apps = _inventory.Current?.Apps ?? [];
        var (uses, packaged) = await Task.Run(() => (ConsentStore.Read(), StoreAddOnManager.DisplayNames()));
        var rows = new List<CapabilityRow>();
        foreach (var u in uses)
        {
            // Name an exe after the installed app it belongs to, and a package by the name you see.
            var name = u.ExePath is { } path
                ? apps.FirstOrDefault(a => a.InstallLocation is { Length: > 3 } loc && path.StartsWith(loc.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))?.Name ?? u.App
                : packaged.GetValueOrDefault(u.App) ?? u.App;
            // One row per app and device: Discord keeps a separate entry for every version folder.
            if (rows.Any(r => r.App == name && r.Capability == u.Capability)) continue;
            var when = u.InUse ? "in use now" : $"last used {Format.Ago(u.LastStopped ?? u.LastStarted)}";
            rows.Add(new CapabilityRow(name, u.Capability, when, u.InUse, u.ExePath));
        }
        Accesses.Clear();
        foreach (var r in rows.Take(40)) Accesses.Add(r);
        HasAccesses = Accesses.Count > 0;
    }

    // ---------- snapshots and Explorer refresh ----------

    private void LoadSnapshots()
    {
        try
        {
            if (_db.GetSetting(SnapshotKey) is { Length: > 0 } json)
                _snapshots = JsonSerializer.Deserialize<Dictionary<string, List<SavedValue>>>(json) ?? new();
        }
        catch (JsonException)
        {
            _snapshots = new();
        }
    }

    private void SaveSnapshots() => _db.SetSetting(SnapshotKey, JsonSerializer.Serialize(_snapshots));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    /// <summary>Tells Explorer and the taskbar that settings changed, so most switches apply right away.</summary>
    private static void BroadcastSettingChange()
    {
        const uint WmSettingChange = 0x001A, SmtoAbortIfHung = 0x0002;
        _ = Task.Run(() => SendMessageTimeout(new IntPtr(0xFFFF), WmSettingChange, IntPtr.Zero, "TraySettings", SmtoAbortIfHung, 1000, out _));
    }
}
