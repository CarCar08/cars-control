using CarsControl.App.Converters;
using CarsControl.Contracts;
using CarsControl.Core.Cleanup;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;

namespace CarsControl.App.Services;

public sealed record CleanupResult(CleanupRule Rule, string Text, long FreedBytes);

public sealed record CleanupRunSummary(string Origin, bool DryRun, IReadOnlyList<CleanupResult> Results, IReadOnlyList<CleanupRule> Waiting)
{
    public long FreedBytes => Results.Sum(r => r.FreedBytes);
}

/// <summary>
/// Runs cleanup for the Cleanup page, scheduled maintenance and approvals. Your-account categories run
/// here, as you; system ones go to the admin helper in one request. Unattended runs never prompt: system
/// categories wait in <see cref="CleanupRunSummary.Waiting"/> unless an approved session is open.
/// </summary>
public sealed class CleanupRunner(HelperClient helper, CarsDb db)
{
    public bool IsRunning { get; private set; }

    /// <summary>Raised on the UI thread.</summary>
    public event Action<bool>? RunningChanged;
    public event Action<CleanupRule>? RuleStarted;
    public event Action<CleanupResult>? RuleFinished;
    public event Action<CleanupRunSummary>? RunFinished;

    public async Task<CleanupRunSummary?> RunAsync(IReadOnlyList<CleanupRule> rules, string origin, bool dryRun = false, bool unattended = false)
    {
        if (IsRunning || rules.Count == 0) return null;
        SetRunning(true);
        var results = new List<CleanupResult>();
        var waiting = new List<CleanupRule>();

        void Done(CleanupRule rule, string text, long freed)
        {
            var r = new CleanupResult(rule, text, freed);
            results.Add(r);
            Platform.OnUiThread(() => RuleFinished?.Invoke(r));
        }

        try
        {
            foreach (var rule in rules.Where(r => !r.Elevated))
            {
                Platform.OnUiThread(() => RuleStarted?.Invoke(rule));
                var log = new List<string>();
                var o = await CleanupRules.RunAsync(rule, log.Add, dryRun, CancellationToken.None);
                Record(rule, o, false, log, dryRun, origin);
                Done(rule, o.Message, o.FreedBytes);
            }

            var admin = rules.Where(r => r.Elevated).ToList();
            if (admin.Count > 0 && unattended && !dryRun && !helper.IsConnected)
            {
                waiting.AddRange(admin);
            }
            else if (admin.Count > 0)
            {
                foreach (var a in admin) Platform.OnUiThread(() => RuleStarted?.Invoke(a));
                try
                {
                    var log = new List<string>();
                    var op = new CleanupRunOp(admin.Select(a => a.Id).ToList());
                    var r = unattended && !dryRun
                        ? await helper.SendIfConnectedAsync(op, log.Add, dryRun)
                        : await helper.SendAsync(op, log.Add, dryRun);
                    var outcomes = r.Data is { } json ? HelperJson.Deserialize<List<CleanupOutcome>>(json) ?? [] : [];
                    foreach (var a in admin)
                    {
                        var o = outcomes.FirstOrDefault(x => x.RuleId == a.Id) ?? new CleanupOutcome(a.Id, 0, 0, 0, r.Message ?? r.Status.ToString());
                        Record(a, o, true, log, dryRun, origin);
                        Done(a, o.Message, o.FreedBytes);
                    }
                }
                catch (HelperSessionClosedException)
                {
                    waiting.AddRange(admin);
                    foreach (var a in admin) Platform.OnUiThread(() => RuleFinished?.Invoke(new CleanupResult(a, "Waiting for admin approval", 0)));
                }
                catch (HelperCancelledException)
                {
                    foreach (var a in admin) Done(a, "Skipped — admin approval was cancelled", 0);
                }
                catch (HelperUnavailableException ex)
                {
                    foreach (var a in admin) Done(a, $"Failed — {ex.Message}", 0);
                }
            }
        }
        finally
        {
            SetRunning(false);
        }

        var summary = new CleanupRunSummary(origin, dryRun, results, waiting);
        Platform.OnUiThread(() => RunFinished?.Invoke(summary));
        return summary;
    }

    /// <summary>One-line result for banners, Home and phone alerts: "freed 2.1 GB".</summary>
    public static string Describe(CleanupRunSummary s) =>
        s.DryRun ? "dry run" : s.FreedBytes > 0 ? $"freed {Format.Bytes(s.FreedBytes)}" : "nothing to clean";

    private void Record(CleanupRule rule, CleanupOutcome o, bool elevated, List<string> log, bool dryRun, string origin)
    {
        if (dryRun) return;
        var status = o.Removed > 0 || o.FreedBytes > 0 || o.Message.StartsWith("Recycle") ? "Succeeded"
            : o.Message.StartsWith("Skipped") ? "Skipped"
            : "Succeeded";
        var message = origin == "Cleanup" ? o.Message : $"{o.Message} ({origin})";
        db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "cleanup", $"cleanup:{rule.Id}", rule.Title, "Cleanup", null, null,
            elevated, status, 0, message, string.Join(Environment.NewLine, log)));
    }

    private void SetRunning(bool value)
    {
        IsRunning = value;
        Platform.OnUiThread(() => RunningChanged?.Invoke(value));
    }
}
