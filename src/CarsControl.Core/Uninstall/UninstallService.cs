using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Models;
using CarsControl.Core.Updates;
using CarsControl.Core.Winget;

namespace CarsControl.Core.Uninstall;

/// <summary>A leftover shown for review. Machine leftovers carry the helper's token; user ones carry
/// the local candidate.</summary>
public sealed record LeftoverEntry(string AppName, LeftoverKind Kind, string Path, long SizeBytes, Confidence Confidence, string Reason, bool IsMachine, string? Token, LeftoverCandidate? Local);

/// <summary>App-side orchestration of bulk uninstall: per-user apps run here as you, machine apps go to
/// the helper. Leftovers are scanned on both sides and quarantined (never deleted outright).</summary>
public sealed class UninstallService(IHelperChannel helper)
{
    public bool DryRun { get; set; }

    public async Task<UninstallResult> UninstallAsync(AppEntry app, UninstallPlan plan, Action<string> line, CancellationToken ct)
    {
        if (!plan.Elevated)
            return await UninstallExecutor.ExecuteAsync(app, plan, WingetCli.Locate(), line, DryRun, ct);

        HelperOp op = plan.Method == UninstallMethod.Winget
            ? new WingetUninstallOp(app.WingetId!, app.Arp!.Hive.ToString(), app.Arp.KeyName)
            : new ArpUninstallOp(app.Arp!.Hive.ToString(), app.Arp.KeyName, Interactive: plan.Method == UninstallMethod.Interactive);

        if (DryRun && !helper.IsConnected && !HelperClient.DevUnelevated)
            return new(UninstallOutcome.DryRun, $"[admin helper] {plan.Description}: {plan.Command?.ToString() ?? UpdateExecutor.DescribeOp(op)}");

        var r = await helper.SendAsync(op, line, DryRun, ct);
        return r.Status switch
        {
            OpStatus.Succeeded => new(UninstallOutcome.Removed, r.Message ?? "Removed", r.ExitCode, r.RebootRequired),
            OpStatus.DryRun => new(UninstallOutcome.DryRun, r.Message ?? ""),
            OpStatus.NotFound => new(UninstallOutcome.Removed, "Already gone"),
            _ => new(UninstallOutcome.Failed, r.Message ?? r.Status.ToString(), r.ExitCode),
        };
    }

    public static Task CreateRestorePointAsync(IHelperChannel helper, bool dryRun, Action<string> line, CancellationToken ct) =>
        helper.SendAsync(new CreateRestorePointOp($"Before C.A.R.S. uninstall {DateTime.Now:yyyy-MM-dd HH.mm}"), line, dryRun, ct);

    /// <summary>User-profile leftovers for any removed app, plus machine leftovers (via the helper) for
    /// machine-wide apps the helper removed this session.</summary>
    public async Task<IReadOnlyList<LeftoverEntry>> ScanLeftoversAsync(AppEntry app, IReadOnlyList<AppEntry> stillInstalled, CancellationToken ct)
    {
        var list = new List<LeftoverEntry>();
        var userScan = await Task.Run(() => new LeftoverScanner(stillInstalled).Scan(app, LeftoverScope.User), ct);
        list.AddRange(userScan.Select(c => new LeftoverEntry(app.Name, c.Kind, c.Path, c.SizeBytes, c.Confidence, c.Reason, false, null, c)));

        if (app.Scope == InstallScope.Machine && app.Arp is { } loc && helper.IsConnected)
        {
            var r = await helper.SendAsync(new LeftoverScanOp(loc.Hive.ToString(), loc.KeyName), null, false, ct);
            if (r.Ok && r.Data is { } json && HelperJson.Deserialize<List<LeftoverItem>>(json) is { } items)
            {
                list.AddRange(items.Select(i => new LeftoverEntry(app.Name, Enum.Parse<LeftoverKind>(i.Kind), i.Path, i.SizeBytes,
                    Enum.Parse<Confidence>(i.Confidence), i.Reason, true, i.Token, null)));
            }
        }
        return list;
    }

    public sealed record QuarantineResult(string? UserQuarantineId, string? MachineQuarantineId, int Count, string? Error);

    public async Task<QuarantineResult> QuarantineAsync(string appName, IReadOnlyList<LeftoverEntry> selected, Action<string> line, CancellationToken ct)
    {
        string? userId = null, machineId = null, error = null;
        var count = 0;

        var user = selected.Where(s => !s.IsMachine && s.Local is not null).Select(s => s.Local!).ToList();
        if (user.Count > 0)
        {
            var m = await Task.Run(() => Quarantine.Move(Quarantine.UserRoot, appName, user, line), ct);
            userId = m.Id;
            count += m.Items.Count;
        }

        var tokens = selected.Where(s => s.IsMachine && s.Token is not null).Select(s => s.Token!).ToList();
        if (tokens.Count > 0)
        {
            var r = await helper.SendAsync(new QuarantineOp(tokens), line, false, ct);
            if (r.Ok) { machineId = r.Data; count += tokens.Count; }
            else error = r.Message;
        }
        return new(userId, machineId, count, error);
    }

    public async Task<string> RestoreAsync(string? userId, string? machineId, Action<string> line, CancellationToken ct)
    {
        var parts = new List<string>();
        if (userId is not null)
        {
            var m = await Task.Run(() => Quarantine.Restore(Quarantine.UserRoot, userId, line), ct);
            parts.Add($"{m.Items.Count} item(s) in your profile");
        }
        if (machineId is not null)
        {
            var r = await helper.SendAsync(new RestoreQuarantineOp(machineId), line, false, ct);
            parts.Add(r.Message ?? r.Status.ToString());
        }
        return "Restored " + string.Join(" and ", parts);
    }
}
