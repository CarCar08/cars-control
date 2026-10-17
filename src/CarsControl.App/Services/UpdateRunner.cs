using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Health;
using CarsControl.Core.Updates;
using CarsControl.Core.Util;

namespace CarsControl.App.Services;

public enum ItemState { Pending, Running, Succeeded, Failed, Skipped }

/// <summary>What happened to one update in a run.</summary>
public sealed record UpdateOutcome(UpdateCandidate Candidate, ItemState State, string StatusText, string? Warning, UpdateRunResult Result, IReadOnlyList<string> Output)
{
    public static string WaitingText => "Waiting for admin approval";
}

public sealed record UpdateRunSummary(string Origin, bool DryRun, bool Cancelled, IReadOnlyList<UpdateOutcome> Outcomes, IReadOnlyList<UpdateCandidate> Waiting)
{
    public int Updated => Outcomes.Count(o => o.State == ItemState.Succeeded && !o.Candidate.IsCheckAction);
    public int Ran => Outcomes.Count(o => o.State == ItemState.Succeeded && o.Candidate.IsCheckAction);
    public int Failed => Outcomes.Count(o => o.State == ItemState.Failed);
    public int Skipped => Outcomes.Count(o => o.State == ItemState.Skipped);

    public bool RestartNeeded => Outcomes.Any(o => o.State == ItemState.Succeeded &&
        (o.Result.RebootRequired || o.StatusText.Contains("restart", StringComparison.OrdinalIgnoreCase)));
}

public enum OpenAppsChoice { CloseThem, SkipThem, Cancel }

public sealed class UpdateRunOptions
{
    /// <summary>Shown in banners and History ("Refresh", "Scheduled maintenance", "After your game"…).</summary>
    public string Origin { get; init; } = "Refresh";

    public bool DryRun { get; init; }

    /// <summary>Nobody is at the PC: admin items run only over an already-approved session (no UAC prompt
    /// ever appears); otherwise they come back in <see cref="UpdateRunSummary.Waiting"/>. Open apps are
    /// skipped, never closed.</summary>
    public bool Unattended { get; init; }

    /// <summary>Asked once per run when apps that would have to close are open (attended runs only).</summary>
    public Func<IReadOnlyList<UpdateCandidate>, Task<OpenAppsChoice>>? AskAboutOpenApps { get; init; }
}

/// <summary>
/// Runs updates for every caller: Refresh, Home's "Update everything", scheduled maintenance, after a game,
/// the command palette and approvals. One run at a time. Every item ends up in History; finished items are
/// dropped from <see cref="UpdateCenter"/> right away ("done means gone"). Refresh shows any run's progress
/// through the events, whoever started it.
/// </summary>
public sealed class UpdateRunner(HelperClient helper, CarsDb db, UpdateCenter center, InventoryStore inventory, AppSettings settings)
{
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }

    /// <summary>All events are raised on the UI thread.</summary>
    public event Action<bool>? RunningChanged;
    public event Action<UpdateCandidate>? ItemStarted;
    public event Action<UpdateCandidate, string>? ItemLine;
    public event Action<UpdateOutcome>? ItemFinished;
    public event Action<double, string?>? ProgressChanged;
    public event Action<UpdateRunSummary>? RunFinished;

    public void Cancel() => _cts?.Cancel();

    /// <summary>Null when another run is in progress or you chose Cancel at the open-apps question.</summary>
    public async Task<UpdateRunSummary?> RunAsync(IReadOnlyList<UpdateCandidate> items, UpdateRunOptions options)
    {
        if (IsRunning || items.Count == 0) return null;

        // Unattended: admin items need a session that's already approved; the rest wait for you.
        var waiting = new List<UpdateCandidate>();
        var protectedWork = items.Where(i => Core.Inventory.ProtectedApps.IsAlwaysProtectedName(i.Title)
            || Core.Inventory.ProtectedApps.IsAlwaysProtectedName(i.PackageId)
            || i.App is { } app && Core.Inventory.ProtectedApps.IsAlwaysProtected(app)).ToList();
        var work = items.Except(protectedWork).ToList();
        if (options.Unattended && !options.DryRun && !helper.IsConnected)
        {
            waiting.AddRange(work.Where(i => i.NeedsAdmin));
            work.RemoveAll(i => i.NeedsAdmin);
        }

        // Apps that are open and would be closed by their installer.
        var skippedOpen = protectedWork.Select(i => (Item: i, Why: "Skipped — Salad is permanently protected; use its own updater when you choose")).ToList();
        if (options.Unattended)
            foreach (var broken in work.Where(KnownBroken).ToList())
            {
                skippedOpen.Add((broken, "Skipped — winget's catalog entry for this version is broken; waiting for a newer one"));
                work.Remove(broken);
            }
        if (!options.DryRun)
        {
            var open = work
                .Where(i => i.Source == UpdateSource.Winget)
                .Select(i => (item: i, procs: RunningApps.InFolder(i.App?.InstallLocation)))
                .Where(x => x.procs.Count > 0)
                .ToList();
            if (open.Count > 0)
            {
                var choice = options.Unattended || options.AskAboutOpenApps is null
                    ? OpenAppsChoice.SkipThem
                    : await options.AskAboutOpenApps(open.Select(o => o.item).ToList());
                switch (choice)
                {
                    case OpenAppsChoice.Cancel:
                        return null;
                    case OpenAppsChoice.SkipThem:
                        skippedOpen.AddRange(open.Select(o => (o.item, "Skipped — the app was open")));
                        break;
                    case OpenAppsChoice.CloseThem:
                        foreach (var (item, procs) in open)
                            if (!await RunningApps.CloseGracefullyAsync(procs, TimeSpan.FromSeconds(15)))
                                skippedOpen.Add((item, "Skipped — the app didn't close"));
                        break;
                }
                work.RemoveAll(i => skippedOpen.Any(s => s.Item == i));
            }
        }

        var unattendedChannel = options.Unattended && !options.DryRun;
        var executor = new UpdateExecutor(unattendedChannel ? helper.ExistingSessionOnly : helper) { DryRun = options.DryRun };
        var outcomes = new List<UpdateOutcome>();
        var cancelled = false;

        SetRunning(true);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Admin work first so the single UAC prompt comes up front; Office last because it runs in the background.
        var order = new[] { UpdateSource.WindowsUpdate, UpdateSource.Store, UpdateSource.Winget, UpdateSource.Pip, UpdateSource.Wsl, UpdateSource.PsModule, UpdateSource.Npm, UpdateSource.Office };
        var queue = work
            .OrderBy(i => i.NeedsAdmin ? 0 : 1)
            .ThenBy(i => Array.IndexOf(order, i.Source))
            .ToList();
        var total = Math.Max(1, queue.Count);
        var done = 0;
        var adminCancelled = false;

        void Report(UpdateCandidate c, UpdateRunResult r, IReadOnlyList<string> output, ItemState? forced = null)
        {
            if (r.Status == OpStatus.Cancelled && r.Message == HelperSessionClosedMessage)
            {
                // The approved session ended mid-run: this one waits for you too.
                waiting.Add(c);
                Raise(() => ItemFinished?.Invoke(new UpdateOutcome(c, ItemState.Pending, UpdateOutcome.WaitingText, null, r, output)));
                return;
            }
            var o = Finish(c, r, output, forced, options);
            outcomes.Add(o);
            Raise(() => ItemFinished?.Invoke(o));
        }

        try
        {
            foreach (var (item, why) in skippedOpen)
                Report(item, new UpdateRunResult(OpStatus.Cancelled, why, false), [], ItemState.Skipped);

            // Windows Update items install in one WUA session.
            var wu = queue.Where(i => i.Source == UpdateSource.WindowsUpdate).ToList();
            if (wu.Count > 0)
            {
                var output = new List<string>();
                foreach (var i in wu) Raise(() => ItemStarted?.Invoke(i));

                // A feature upgrade or a driver can't be undone by hand: make a restore point first (when you want one).
                var restoreCancelled = false;
                RiskyChange? risky = wu.Any(i => SafetyNet.IsFeatureUpdate(i.Title)) ? RiskyChange.FeatureUpdate : wu.Any(i => i.IsDriver) ? RiskyChange.DriverUpdate : null;
                if (risky is { } change && !options.DryRun)
                {
                    Progress(done, total, "Making a restore point first…");
                    var detail = wu.First(i => change == RiskyChange.FeatureUpdate ? SafetyNet.IsFeatureUpdate(i.Title) : i.IsDriver).Title;
                    var rp = await SafetyNet.MakeAsync(unattendedChannel ? helper.ExistingSessionOnly : helper, settings.Store.Current.Health, SafetyNet.Read(), change, detail, HelperClient.DevUnelevated, ct);
                    switch (rp.Outcome)
                    {
                        case RestoreOutcome.Made: Line(wu[0], output, "A restore point was made first."); break;
                        case RestoreOutcome.NoProtection: Line(wu[0], output, rp.Message ?? "No restore point was made."); break;
                        case RestoreOutcome.Failed: Line(wu[0], output, "No restore point could be made (" + (rp.Message ?? "unknown reason") + "); going on without one."); break;
                        case RestoreOutcome.Cancelled: restoreCancelled = !unattendedChannel; break;
                    }
                }

                if (restoreCancelled)
                {
                    foreach (var i in wu) Report(i, new UpdateRunResult(OpStatus.Cancelled, "Skipped — admin approval was cancelled", true), [], ItemState.Skipped);
                    adminCancelled = true;
                }
                else
                {
                    Progress(done, total, $"Installing {wu.Count} Windows update(s)…");
                    var r = await SafeRun(true, () => executor.WindowsUpdatesAsync(wu, l => Line(wu[0], output, l), ct));
                    foreach (var i in wu) Report(i, r, output);
                    if (r.Status == OpStatus.Cancelled && r.Message.Contains("approval")) adminCancelled = true;
                }
                done += wu.Count;
            }

            foreach (var item in queue.Where(i => i.Source != UpdateSource.WindowsUpdate))
            {
                ct.ThrowIfCancellationRequested();
                if (item.NeedsAdmin && adminCancelled)
                {
                    Report(item, new UpdateRunResult(OpStatus.Cancelled, "Skipped — admin approval was cancelled", true), [], ItemState.Skipped);
                }
                else
                {
                    var output = new List<string>();
                    Raise(() => ItemStarted?.Invoke(item));
                    Progress(done, total, $"Updating {item.Title} ({done + 1} of {queue.Count})");
                    var r = await SafeRun(item.NeedsAdmin, () => executor.RunAsync(item, l => Line(item, output, l), ct));
                    if (r.Status == OpStatus.Cancelled && item.NeedsAdmin && r.Message.Contains("approval")) adminCancelled = true;
                    Report(item, r, output);
                }
                done++;
                Progress(done, total, null);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            foreach (var i in queue.Where(i => outcomes.All(o => o.Candidate != i) && !waiting.Contains(i)))
                Report(i, new UpdateRunResult(OpStatus.Cancelled, "Stopped", false), [], ItemState.Skipped);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
            Progress(0, 1, null);
        }

        var summary = new UpdateRunSummary(options.Origin, options.DryRun, cancelled, outcomes, waiting);
        if (!options.DryRun)
        {
            center.MarkDone(outcomes.Where(o => o.State == ItemState.Succeeded && !o.Candidate.IsCheckAction).Select(o => o.Candidate));
            _ = inventory.RefreshAsync();
        }
        Raise(() => RunFinished?.Invoke(summary));
        return summary;
    }

    private const string HelperSessionClosedMessage = "Waiting — no admin session was open";

    private static async Task<UpdateRunResult> SafeRun(bool needsAdmin, Func<Task<UpdateRunResult>> run)
    {
        try
        {
            return await run();
        }
        catch (HelperSessionClosedException)
        {
            return new UpdateRunResult(OpStatus.Cancelled, HelperSessionClosedMessage, true);
        }
        catch (HelperCancelledException)
        {
            return new UpdateRunResult(OpStatus.Cancelled, "Skipped — admin approval was cancelled", true);
        }
        catch (HelperUnavailableException ex)
        {
            return new UpdateRunResult(OpStatus.Failed, ex.Message, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UpdateRunResult(OpStatus.Failed, ex.Message, needsAdmin);
        }
    }

    private void Line(UpdateCandidate c, List<string> output, string text)
    {
        lock (output) output.Add(text);
        Raise(() => ItemLine?.Invoke(c, text));
    }

    private UpdateOutcome Finish(UpdateCandidate c, UpdateRunResult r, IReadOnlyList<string> output, ItemState? forced, UpdateRunOptions options)
    {
        var state = forced ?? (r.Ok ? ItemState.Succeeded : r.Status == OpStatus.Cancelled ? ItemState.Skipped : ItemState.Failed);
        var status = r.Status == OpStatus.DryRun ? $"Would run: {r.Message}" : r.Message;
        var warning = r.Warnings is { Count: > 0 } w ? string.Join(" ", w) : null;
        if (warning is not null && state == ItemState.Succeeded)
        {
            state = ItemState.Failed;
            status = "Not updated";
        }
        // A broken catalog entry isn't retried by unattended runs until winget offers something newer (or you try it by hand).
        if (r.Mismatch is not null && c.AvailableVersion is not null) db.SetSetting(BrokenCatalogKey(c), c.AvailableVersion);

        if (r.Status != OpStatus.DryRun)
        {
            string text;
            lock (output) text = string.Join(Environment.NewLine, output);
            var message = warning is null ? r.Message : $"{r.Message} — {warning}";
            if (options.Origin != "Refresh") message += $" ({options.Origin})";
            db.AddHistory(new HistoryEntry(
                0, DateTime.Now.AddSeconds(-1), DateTime.Now, "update", c.Key, c.Title, c.Source.ToString(),
                c.CurrentVersion, c.AvailableVersion, r.Elevated,
                state == ItemState.Skipped ? "Skipped" : r.Status.ToString(), r.ExitCode, message, text));
        }
        return new UpdateOutcome(c, state, status, warning, r, output);
    }

    private static string BrokenCatalogKey(UpdateCandidate c) => "broken-catalog:" + c.Key;

    /// <summary>True when this exact version already failed because winget's catalog entry is broken.</summary>
    public bool KnownBroken(UpdateCandidate c) => c.AvailableVersion is not null && db.GetSetting(BrokenCatalogKey(c)) == c.AvailableVersion;

    private void SetRunning(bool value)
    {
        IsRunning = value;
        Raise(() => RunningChanged?.Invoke(value));
    }

    private void Progress(int done, int total, string? text) => Raise(() => ProgressChanged?.Invoke(done * 100.0 / total, text));

    private static void Raise(Action a) => Platform.OnUiThread(a);
}
