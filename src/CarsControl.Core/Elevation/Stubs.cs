using CarsControl.Contracts;
using CarsControl.Core.Cleanup;

namespace CarsControl.Core.Elevation;

/// <summary>Elevated cleanup inside the helper: only rule ids defined in <see cref="CleanupRules"/> with
/// Elevated = true are accepted; per-user rules never run as admin.</summary>
internal static class CleanupOps
{
    public static async Task<OpResult> ExecuteAsync(HelperOp op, bool dryRun, Action<string> line, HelperLog log, CancellationToken ct)
    {
        var ids = op switch
        {
            CleanupRunOp r => r.RuleIds,
            CleanupEstimateOp e => e.RuleIds,
            _ => [],
        };
        var rules = new List<CleanupRule>();
        foreach (var id in ids)
        {
            if (CleanupRules.Find(id) is not { Elevated: true } rule) return OpResult.Reject($"Unknown or non-admin cleanup rule '{id}'.");
            rules.Add(rule);
        }

        if (op is CleanupEstimateOp)
        {
            var estimates = rules.Select(CleanupRules.Estimate).ToList();
            return OpResult.Success("estimated", HelperJson.Serialize(estimates));
        }

        var outcomes = new List<CleanupOutcome>();
        foreach (var rule in rules)
        {
            log.Write($"cleanup {rule.Id} dryRun={dryRun}");
            outcomes.Add(await CleanupRules.RunAsync(rule, line, dryRun, ct));
        }
        return new OpResult(dryRun ? OpStatus.DryRun : OpStatus.Succeeded, 0, string.Join("; ", outcomes.Select(o => o.Message)), Data: HelperJson.Serialize(outcomes));
    }
}
