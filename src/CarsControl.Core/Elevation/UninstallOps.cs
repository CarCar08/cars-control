using System.Security.Cryptography;
using CarsControl.Contracts;
using CarsControl.Core.Inventory;
using CarsControl.Core.Models;
using CarsControl.Core.Uninstall;
using CarsControl.Core.Winget;

namespace CarsControl.Core.Elevation;

/// <summary>
/// Machine-wide uninstall, leftover scan and quarantine inside the helper. Trust model:
///   - Only HKLM entries are accepted; the helper reads the entry and plans the method itself.
///   - Before uninstalling, it snapshots the entry; leftover scans use only that snapshot.
///   - Scan results are handed out as random tokens; quarantine accepts only tokens from this session.
///   - "Other installed apps" (which must never be touched) come from HKLM + your HKCU via HKEY_USERS.
/// </summary>
internal sealed class UninstallOps(HelperLog log, string? parentProfile, string? parentSid)
{
    private readonly Dictionary<string, AppEntry> _removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (LeftoverCandidate Item, string AppName)> _tokens = new();

    public async Task<OpResult> ExecuteAsync(HelperOp op, bool dryRun, Action<string> line, CancellationToken ct) => op switch
    {
        ArpUninstallOp u => await UninstallAsync(u, dryRun, line, ct),
        WingetUninstallOp w => await WingetUninstallAsync(w, dryRun, line, ct),
        LeftoverScanOp s => Scan(s),
        QuarantineOp q => QuarantineItems(q, line),
        RestoreQuarantineOp r => RestoreItems(r, line),
        _ => OpResult.Reject("Unsupported uninstall operation"),
    };

    private static ArpHive? ParseMachineHive(string hive) => hive switch
    {
        nameof(ArpHive.LocalMachine64) => ArpHive.LocalMachine64,
        nameof(ArpHive.LocalMachine32) => ArpHive.LocalMachine32,
        _ => null, // CurrentUser and anything else: refused
    };

    private async Task<OpResult> UninstallAsync(ArpUninstallOp u, bool dryRun, Action<string> line, CancellationToken ct)
    {
        if (ParseMachineHive(u.Hive) is not { } hive) return OpResult.Reject("Only machine-wide (HKLM) apps are uninstalled by the helper.");
        if (u.KeyName.Length is 0 or > 260 || u.KeyName.Contains('\\')) return OpResult.Reject("Invalid key name.");

        var loc = new ArpLocation(hive, u.KeyName);
        var entry = ArpProvider.ReadOne(loc);
        if (entry is null) return new OpResult(OpStatus.NotFound, 0, "Not installed (already removed?)");
        if (ProtectedApps.IsAlwaysProtected(entry)) return OpResult.Reject("Salad is permanently protected and cannot be changed.");

        // winget id isn't in the registry; the helper only uses winget when the vendor gave no silent path.
        var plan = UninstallPlanner.Plan(entry);
        if (plan.Method == UninstallMethod.Interactive && !u.Interactive)
            return OpResult.Reject("This app has no silent uninstaller; it must be confirmed as interactive.");
        if (plan.Method is UninstallMethod.None or UninstallMethod.Msix or UninstallMethod.SteamHandoff)
            return OpResult.Reject($"Not a helper uninstall: {plan.Description}");

        line($"plan: {plan.Description}");
        if (!dryRun) _removed[$"{hive}\\{u.KeyName}"] = entry;

        var winget = WingetCli.Locate(parentProfile);
        var r = await UninstallExecutor.ExecuteAsync(entry, plan, winget, line, dryRun, ct);
        var status = r.Outcome switch
        {
            UninstallOutcome.Removed => OpStatus.Succeeded,
            UninstallOutcome.DryRun => OpStatus.DryRun,
            _ => OpStatus.Failed,
        };
        return new OpResult(status, r.ExitCode, r.Message, r.RebootRequired);
    }

    private async Task<OpResult> WingetUninstallAsync(WingetUninstallOp w, bool dryRun, Action<string> line, CancellationToken ct)
    {
        if (!WingetCli.IsValidId(w.Id)) return OpResult.Reject($"Invalid winget id '{w.Id}'.");
        if (ProtectedApps.IsAlwaysProtectedName(w.Id)) return OpResult.Reject("Salad is permanently protected and cannot be changed.");
        AppEntry? snapshot = null;
        if (w.Hive is not null && w.KeyName is not null)
        {
            if (ParseMachineHive(w.Hive) is not { } hive) return OpResult.Reject("Only machine-wide (HKLM) apps are uninstalled by the helper.");
            snapshot = ArpProvider.ReadOne(new ArpLocation(hive, w.KeyName));
            if (snapshot is null) return new OpResult(OpStatus.NotFound, 0, "Not installed (already removed?)");
            if (ProtectedApps.IsAlwaysProtected(snapshot)) return OpResult.Reject("Salad is permanently protected and cannot be changed.");
            snapshot.WingetId = w.Id;
        }

        var winget = WingetCli.Locate(parentProfile);
        if (winget is null) return new OpResult(OpStatus.Failed, -1, "winget.exe was not found for the admin account.");
        var args = WingetCli.UninstallArgs(w.Id, null);
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, HelperOperations.Describe(winget, args));

        if (snapshot is not null)
        {
            _removed[$"{snapshot.Arp!.Hive}\\{snapshot.Arp.KeyName}"] = snapshot;
            var r = await UninstallExecutor.ExecuteAsync(snapshot, new UninstallPlan(UninstallMethod.Winget, true, "winget"), winget, line, false, ct);
            return new OpResult(r.Outcome == UninstallOutcome.Removed ? OpStatus.Succeeded : OpStatus.Failed, r.ExitCode, r.Message, r.RebootRequired);
        }
        var (outcome, raw) = await WingetCli.RunAsync(winget, args, line, ct);
        return new OpResult(outcome.Ok ? OpStatus.Succeeded : OpStatus.Failed, raw.ExitCode, outcome.Message, outcome.RebootRequired);
    }

    private OpResult Scan(LeftoverScanOp s)
    {
        if (!_removed.TryGetValue($"{s.Hive}\\{s.KeyName}", out var app))
            return OpResult.Reject("Leftover scans are only allowed for apps this admin session removed.");

        var others = ArpProvider.ReadMachineAndUser(parentSid);
        var candidates = new LeftoverScanner(others).Scan(app, LeftoverScope.Machine);
        var items = candidates.Select(c =>
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _tokens[token] = (c, app.Name);
            return new LeftoverItem(token, c.Kind.ToString(), c.Path, c.SizeBytes, c.Confidence.ToString(), c.Reason);
        }).ToList();
        log.Write($"leftover scan {app.Name}: {items.Count} candidate(s)");
        return OpResult.Success($"{items.Count} candidate(s)", HelperJson.Serialize(items));
    }

    private OpResult QuarantineItems(QuarantineOp q, Action<string> line)
    {
        var picked = new List<(LeftoverCandidate Item, string AppName)>();
        foreach (var t in q.Tokens)
        {
            if (!_tokens.Remove(t, out var hit)) return OpResult.Reject("Unknown leftover token — rescan first.");
            picked.Add(hit);
        }
        if (picked.Count == 0) return OpResult.Reject("Nothing selected.");

        var root = SecureDataDir.Ensure("Quarantine");
        Quarantine.Purge(root, TimeSpan.FromDays(30));
        var manifest = Quarantine.Move(root, picked[0].AppName, picked.Select(p => p.Item).ToList(), line);
        return OpResult.Success($"Quarantined {manifest.Items.Count} item(s)", manifest.Id);
    }

    private static OpResult RestoreItems(RestoreQuarantineOp r, Action<string> line)
    {
        if (!Quarantine.IsValidId(r.QuarantineId)) return OpResult.Reject("Invalid quarantine id.");
        var root = SecureDataDir.Ensure("Quarantine");
        if (!Directory.Exists(Path.Combine(root, r.QuarantineId))) return new OpResult(OpStatus.NotFound, 0, "That quarantine no longer exists (purged after 30 days?)");
        var m = Quarantine.Restore(root, r.QuarantineId, line);
        return OpResult.Success($"Restored {m.Items.Count} item(s) for {m.AppName}");
    }
}
