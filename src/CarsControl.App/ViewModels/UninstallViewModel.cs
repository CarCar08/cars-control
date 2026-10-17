using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Models;
using CarsControl.Core.Uninstall;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public enum UninstallStep { Review, Running, Leftovers, Done }

public sealed partial class UninstallRowViewModel(AppEntry app, UninstallPlan plan) : ObservableObject
{
    public AppEntry App { get; } = app;
    public UninstallPlan Plan { get; } = plan;
    public string Name => App.Name;
    public string MethodText => Plan.Description;
    public bool NeedsAdmin => Plan.Elevated;
    public bool IsInteractive => Plan.Method == UninstallMethod.Interactive;
    public bool IsSteam => Plan.Method == UninstallMethod.SteamHandoff;
    public string SizeText => Format.Bytes(App.SizeBytes);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsOk), nameof(IsFailed))]
    private ItemState _state;

    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private string? _lastLine;
    public UninstallResult? Result { get; set; }

    public bool IsRunning => State == ItemState.Running;
    public bool IsOk => State == ItemState.Succeeded;
    public bool IsFailed => State == ItemState.Failed;
}

public sealed partial class LeftoverRowViewModel(LeftoverEntry entry) : ObservableObject
{
    public LeftoverEntry Entry { get; } = entry;
    public string AppName => Entry.AppName;
    public string Path => Entry.Path;
    public string KindText => Entry.Kind switch { LeftoverKind.Folder => "Folder", LeftoverKind.RegistryKey => "Registry", _ => "Shortcut" };
    public string SizeText => Entry.Kind == LeftoverKind.Folder ? Format.Bytes(Entry.SizeBytes) : "";
    public string ConfidenceText => Entry.Confidence.ToString();
    public string Reason => Entry.Reason + (Entry.IsMachine ? " · all users" : "");

    [ObservableProperty] private bool _isSelected = entry.Confidence == Confidence.High;
}

public sealed partial class UninstallViewModel : ObservableObject
{
    private readonly HelperClient _helper;
    private readonly CarsDb _db;
    private readonly UninstallService _service;
    private readonly IReadOnlyList<AppEntry> _allApps;
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<UninstallRowViewModel> Apps { get; } = [];
    public ObservableCollection<LeftoverRowViewModel> Leftovers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReview), nameof(IsRunning), nameof(IsLeftovers), nameof(IsDone))]
    private UninstallStep _step;

    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private bool _dryRun;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _leftoverSummary = "";
    [ObservableProperty] private string _doneSummary = "";
    [ObservableProperty] private double _progress;

    public bool IsReview => Step == UninstallStep.Review;
    public bool IsRunning => Step == UninstallStep.Running;
    public bool IsLeftovers => Step == UninstallStep.Leftovers;
    public bool IsDone => Step == UninstallStep.Done;

    public bool AnyMachine => Apps.Any(a => a.NeedsAdmin);
    public bool AnyInteractive => Apps.Any(a => a.IsInteractive);

    /// <summary>Raised when the window should close; true if anything changed (inventory refresh).</summary>
    public event Action<bool>? CloseRequested;

    public UninstallViewModel(IEnumerable<AppEntry> selected, IReadOnlyList<AppEntry> allApps, HelperClient helper, CarsDb db)
    {
        _helper = helper;
        _db = db;
        _allApps = allApps;
        _service = new UninstallService(helper);
        foreach (var a in selected) Apps.Add(new UninstallRowViewModel(a, UninstallPlanner.Plan(a)));

        CreateRestorePoint = AnyMachine;
        var admin = Apps.Count(a => a.NeedsAdmin);
        Title = $"Uninstall {Apps.Count} app{(Apps.Count == 1 ? "" : "s")}";
        Subtitle = $"{Format.Bytes(Apps.Sum(a => a.App.SizeBytes ?? 0))} · "
            + (admin > 0 ? $"{admin} need admin — you'll be asked once" : "no admin needed")
            + (AnyInteractive ? $" · {Apps.Count(a => a.IsInteractive)} will show their own uninstaller" : "");
    }

    private bool _leftoversOnly;
    private string? _leftoverNote;

    /// <summary>History › Find leftovers: looks for what an app removed earlier left behind (your profile
    /// and Steam's folders; machine-wide folders are only scanned right after an uninstall in this session).</summary>
    public static UninstallViewModel ForLeftovers(AppEntry removed, IReadOnlyList<AppEntry> allApps, HelperClient helper, CarsDb db)
    {
        var vm = new UninstallViewModel([removed], allApps, helper, db) { _leftoversOnly = true };
        var row = vm.Apps[0];
        row.Result = new UninstallResult(UninstallOutcome.Removed, "Uninstalled earlier");
        row.State = ItemState.Succeeded;
        row.StatusText = "Uninstalled earlier";
        vm.Title = $"Leftovers of {removed.Name}";
        vm.Subtitle = "Looking for leftovers…";
        vm.Step = UninstallStep.Running;
        return vm;
    }

    public Task FindLeftoversAsync() => ScanLeftoversAsync(_cts.Token);

    partial void OnDryRunChanged(bool value) => _service.DryRun = value;

    [RelayCommand]
    private async Task StartAsync()
    {
        Step = UninstallStep.Running;
        var ct = _cts.Token;
        var adminCancelled = false;

        if (CreateRestorePoint && AnyMachine && !DryRun)
        {
            Subtitle = "Creating a restore point…";
            try
            {
                await UninstallService.CreateRestorePointAsync(_helper, false, _ => { }, ct);
            }
            catch (HelperCancelledException)
            {
                adminCancelled = true;
            }
            catch (Exception ex)
            {
                Subtitle = $"Restore point failed ({ex.Message}); continuing.";
            }
        }

        // Admin work first (one prompt up front), interactive uninstallers and Steam hand-offs last.
        var queue = Apps
            .OrderBy(a => a.Plan.Method is UninstallMethod.Interactive or UninstallMethod.SteamHandoff ? 1 : 0)
            .ThenBy(a => a.NeedsAdmin ? 0 : 1)
            .ToList();
        var done = 0;
        foreach (var row in queue)
        {
            if (ct.IsCancellationRequested) break;
            if (row.NeedsAdmin && adminCancelled)
            {
                Finish(row, new UninstallResult(UninstallOutcome.Failed, "Skipped — admin approval was cancelled"), ItemState.Skipped);
                continue;
            }
            row.State = ItemState.Running;
            row.StatusText = row.IsInteractive ? "Waiting for the app's uninstaller — finish it in its own window"
                : row.IsSteam ? "Confirm the uninstall in Steam's window — this waits until Steam has removed it"
                : "Uninstalling…";
            Subtitle = $"Removing {row.Name} ({done + 1} of {queue.Count})";
            try
            {
                var r = await _service.UninstallAsync(row.App, row.Plan, l => Platform.OnUiThread(() => row.LastLine = l), ct);
                Finish(row, r);
            }
            catch (HelperCancelledException)
            {
                adminCancelled = true;
                Finish(row, new UninstallResult(UninstallOutcome.Failed, "Skipped — admin approval was cancelled"), ItemState.Skipped);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Finish(row, new UninstallResult(UninstallOutcome.Failed, ex.Message));
            }
            done++;
            Progress = done * 100.0 / queue.Count;
        }

        await ScanLeftoversAsync(ct);
    }

    private void Finish(UninstallRowViewModel row, UninstallResult r, ItemState? forced = null)
    {
        row.Result = r;
        row.State = forced ?? (r.Ok ? ItemState.Succeeded : ItemState.Failed);
        row.StatusText = r.Outcome == UninstallOutcome.DryRun ? $"Would run: {r.Message}" : r.Message;
        row.LastLine = null;
        if (r.Outcome == UninstallOutcome.DryRun) return;
        _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "uninstall", row.App.Key, row.Name, row.Plan.Method.ToString(),
            row.App.Version, null, row.NeedsAdmin, row.State == ItemState.Skipped ? "Skipped" : r.Ok ? "Succeeded" : "Failed", r.ExitCode, r.Message, null,
            Data: r.Outcome == UninstallOutcome.Removed ? RemovedApp.ToData(row.App) : null));
    }

    private async Task ScanLeftoversAsync(CancellationToken ct)
    {
        var removed = Apps.Where(a => a.Result?.Outcome == UninstallOutcome.Removed).ToList();
        var handedOff = Apps.Count(a => a.Result?.Outcome == UninstallOutcome.HandedOff);
        if (removed.Count == 0)
        {
            _leftoverNote = handedOff > 0 ? "Leftovers weren't checked because Steam hasn't finished — use History › Find leftovers later." : null;
            FinishUp();
            return;
        }

        Subtitle = "Looking for leftovers…";
        var removedKeys = removed.Select(r => r.App.Key).ToHashSet();
        var stillInstalled = _allApps.Where(a => !removedKeys.Contains(a.Key)).ToList();
        foreach (var row in removed)
        {
            try
            {
                foreach (var e in await _service.ScanLeftoversAsync(row.App, stillInstalled, ct))
                    Leftovers.Add(new LeftoverRowViewModel(e));
            }
            catch (Exception ex)
            {
                row.StatusText += $" (leftover scan failed: {ex.Message})";
            }
        }

        if (Leftovers.Count == 0)
        {
            // Say so: round 2 skipped straight to "Done" and it looked like leftovers were never checked.
            _leftoverNote = removed.All(r => r.Plan.Method == UninstallMethod.Msix)
                ? "No leftovers found (Store apps remove their own data)."
                : "No leftovers found in your profile, Steam folders or the registry.";
            FinishUp();
            return;
        }
        foreach (var l in Leftovers) l.PropertyChanged += (_, _) => UpdateLeftoverSummary();
        UpdateLeftoverSummary();
        Step = UninstallStep.Leftovers;
    }

    private void UpdateLeftoverSummary()
    {
        var sel = Leftovers.Where(l => l.IsSelected).ToList();
        LeftoverSummary = $"{sel.Count} of {Leftovers.Count} selected · {Format.Bytes(sel.Sum(l => l.Entry.SizeBytes))} · moved to quarantine, restorable from History for 30 days";
    }

    [RelayCommand]
    private async Task QuarantineAsync()
    {
        Subtitle = "Moving leftovers to quarantine…";
        var log = new List<string>();
        var moved = 0;
        foreach (var group in Leftovers.Where(l => l.IsSelected).GroupBy(l => l.AppName))
        {
            var r = await _service.QuarantineAsync(group.Key, group.Select(g => g.Entry).ToList(), log.Add, _cts.Token);
            moved += r.Count;
            _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "leftovers", $"leftovers:{group.Key}", group.Key, "Quarantine",
                null, null, r.MachineQuarantineId is not null, r.Error is null ? "Succeeded" : "Failed", 0,
                r.Error ?? $"Quarantined {r.Count} item(s) — restorable for 30 days", string.Join(Environment.NewLine, log),
                Data: JsonSerializer.Serialize(new { user = r.UserQuarantineId, machine = r.MachineQuarantineId })));
        }
        _leftoverNote = $"Moved {moved} leftover item(s) to quarantine — restorable from History for 30 days.";
        FinishUp();
    }

    [RelayCommand]
    private void SkipLeftovers()
    {
        _leftoverNote = "Leftovers were left in place.";
        FinishUp();
    }

    private void FinishUp()
    {
        var ok = Apps.Count(a => a.Result?.Outcome == UninstallOutcome.Removed);
        var waiting = Apps.Count(a => a.Result?.Outcome == UninstallOutcome.HandedOff);
        var failed = Apps.Count(a => a.State == ItemState.Failed);
        var reboot = Apps.Any(a => a.Result?.RebootRequired == true);
        DoneSummary = DryRun ? "Dry run finished — nothing was changed."
            : _leftoversOnly ? (_leftoverNote ?? "Done.")
            : $"{ok} removed" + (waiting > 0 ? $", {waiting} still waiting on Steam" : "") + (failed > 0 ? $", {failed} failed" : "")
              + (reboot ? " — restart to finish" : "") + "." + (_leftoverNote is { } note ? " " + note : "");
        Subtitle = DoneSummary;
        Step = UninstallStep.Done;
    }

    [RelayCommand]
    private void Close()
    {
        _cts.Cancel();
        CloseRequested?.Invoke(!DryRun && Apps.Any(a => a.State != ItemState.Pending));
    }
}
