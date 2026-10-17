using System.IO;
using System.Windows.Threading;
using CarsControl.Core.Backup;
using CarsControl.Core.Data;
using CarsControl.Core.Settings;

namespace CarsControl.App.Services;

public sealed record JobRunInfo(string JobId, bool IsRunning, string? Status, double Percent);

/// <summary>
/// Runs the backup jobs: on a schedule while you're away (never during a game), and straight away when a USB drive
/// that a job is set up for is plugged in. As you: the folders it reads are yours. A job's result is remembered, shown
/// in History, and a failure reaches Home and, if you set them up, your phone.
/// </summary>
public sealed class BackupService
{
    private readonly AppSettings _settings;
    private readonly CarsDb _db;
    private readonly GameMode _game;
    private readonly AttentionService _attention;
    private readonly AlertService _alerts;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(45) };
    private readonly HashSet<string> _running = [];
    private readonly HashSet<string> _seenUsb = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _cancels = [];

    public BackupService(AppSettings settings, CarsDb db, GameMode game, AttentionService attention, AlertService alerts)
    {
        _settings = settings;
        _db = db;
        _game = game;
        _attention = attention;
        _alerts = alerts;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public event Action<JobRunInfo>? Progress;
    public event Action? Changed;

    public IReadOnlyList<BackupJobSettings> Jobs => _settings.Store.Current.Backup.Jobs;

    public bool IsRunning(string id) => _running.Contains(id);

    public void Start()
    {
        if (App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEV_BACKUP") != "1") return;
        foreach (var serial in Volumes.Serials()) _seenUsb.Add(serial);
        _timer.Start();
        UpdateAttention();
    }

    public void Stop()
    {
        _timer.Stop();
        foreach (var cancel in _cancels.Values) cancel.Cancel();
    }

    // ---------- where to ----------

    /// <summary>The folder a job writes into, or why it can't right now (USB not plugged in).</summary>
    public static (string? Path, string? Problem, VolumeInfo? Usb) Destination(BackupJobSettings job)
    {
        if (job.Kind == "usb")
        {
            if (string.IsNullOrWhiteSpace(job.UsbSerial)) return (null, "No USB drive is chosen for this backup.", null);
            if (Volumes.Find(job.UsbSerial) is not { } v) return (null, $"“{job.UsbLabel ?? "The USB drive"}” isn't plugged in.", null);
            var sub = new string((job.UsbSubfolder ?? "C.A.R.S. Backup").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
            return (Path.Combine(v.Root, sub.Length == 0 ? "C.A.R.S. Backup" : sub, Safe(job.Name)), null, v);
        }
        return string.IsNullOrWhiteSpace(job.Folder) ? (null, "Choose where this backup goes.", null) : (Path.Combine(job.Folder, Safe(job.Name)), null, null);
    }

    private static string Safe(string name)
    {
        var s = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return s.Length == 0 ? "Backup" : s;
    }

    /// <summary>A job set up so the copy sits on the same physical disk as its source is flagged: one failing disk takes both.</summary>
    public static string? SameDiskWarning(BackupJobSettings job)
    {
        var (dest, _, _) = Destination(job);
        if (dest is null) return null;
        foreach (var s in job.Sources)
            if (Volumes.SameDisk(s, dest)) return $"“{s}” and this backup are on the same disk, so if that disk fails you lose both. Pick a different drive, or a cloud or USB destination.";
        return null;
    }

    // ---------- running ----------

    public async Task<BackupResult?> RunAsync(string jobId, string origin)
    {
        var job = Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null || !_running.Add(jobId)) return null;
        var cts = new CancellationTokenSource();
        _cancels[jobId] = cts;
        Platform.OnUiThread(() => Progress?.Invoke(new JobRunInfo(jobId, true, "Starting…", 0)));
        try
        {
            var (dest, problem, usb) = Destination(job);
            if (dest is null) return Finish(job, origin, null, problem ?? "No destination.");
            var options = new BackupOptions(job.Sources, dest, job.KeepDays, job.Excludes);
            if (BackupEngine.Validate(options) is { } invalid) return Finish(job, origin, null, invalid);

            var progress = new Progress<BackupProgress>(p => Progress?.Invoke(new JobRunInfo(jobId, true, $"{p.Source}: {p.FilesDone:N0} files" + (p.Current is null ? "" : $" · {System.IO.Path.GetFileName(p.Current)}"), 0)));
            var result = await BackupEngine.RunAsync(options, progress, cts.Token);
            return Finish(job, origin, result, null, usb is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Finish(job, origin, null, ex.Message);
        }
        finally
        {
            _running.Remove(jobId);
            _cancels.Remove(jobId);
            cts.Dispose();
            Platform.OnUiThread(() => Progress?.Invoke(new JobRunInfo(jobId, false, null, 0)));
        }
    }

    public void Cancel(string jobId)
    {
        if (_cancels.TryGetValue(jobId, out var c)) c.Cancel();
    }

    private BackupResult? Finish(BackupJobSettings job, string origin, BackupResult? result, string? problem, bool toUsb = false)
    {
        var ok = result is { Ok: true };
        var text = problem ?? result!.Summary;
        _settings.Store.Update(s =>
        {
            if (s.Backup.Jobs.FirstOrDefault(j => j.Id == job.Id) is not { } j) return;
            j.LastRunUtc = DateTime.UtcNow;
            j.LastOk = ok;
            j.LastResult = text;
        });
        _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "backup", $"backup:{job.Id}", job.Name, "Backup", null, null, false,
            ok ? "Succeeded" : result?.Cancelled == true ? "Cancelled" : "Failed", ok ? 0 : 1, $"{origin}: {text}", result is { Problems.Count: > 0 } ? string.Join(Environment.NewLine, result.Problems) : null));
        if (!ok && result?.Cancelled != true && origin != "Run now") _ = _alerts.ReportBackupProblemAsync();
        if (ok && toUsb) Notifier.Info("Backup finished: safe to unplug", $"“{job.Name}” is on the USB drive. {text}");
        UpdateAttention();
        Platform.OnUiThread(() => Changed?.Invoke());
        return result;
    }

    // ---------- schedule and USB ----------

    private async Task TickAsync()
    {
        try
        {
            // Nothing to watch for unless a job is set up: a PC without backups does no work here.
            if (!Jobs.Any(j => j.Enabled)) return;
            var present = Volumes.Serials().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var arrived = present.Where(k => !_seenUsb.Contains(k)).ToList();
            foreach (var k in arrived) _seenUsb.Add(k);
            foreach (var gone in _seenUsb.Where(k => !present.Contains(k)).ToList()) _seenUsb.Remove(gone);

            if (_game.IsOn) return;
            foreach (var job in Jobs.Where(j => j.Enabled && !_running.Contains(j.Id)).ToList())
            {
                var due = job.LastRunUtc is null || DateTime.UtcNow - job.LastRunUtc >= TimeSpan.FromHours(Math.Clamp(job.IntervalHours, 1, 24 * 30));
                var plugged = job.Kind == "usb" && job.UsbSerial is not null && arrived.Contains(job.UsbSerial, StringComparer.OrdinalIgnoreCase);
                // Plugging in the drive is "you're here and want it done": run if it's been over an hour since the last one.
                if (plugged && (job.LastRunUtc is null || DateTime.UtcNow - job.LastRunUtc > TimeSpan.FromHours(1)))
                {
                    await RunAsync(job.Id, "Drive plugged in");
                    continue;
                }
                if (!due || Destination(job).Path is null) continue;
                if (UserIdle.Current < TimeSpan.FromMinutes(Math.Max(1, _settings.Store.Current.Maintenance.IdleMinutes)) || UserIdle.FullScreenBusy) continue;
                await RunAsync(job.Id, "Scheduled");
            }
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("backup", ex);
        }
    }

    // ---------- Home ----------

    private void UpdateAttention()
    {
        var failing = Jobs.Where(j => j.Enabled && j.LastOk == false).ToList();
        var stale = Jobs.Where(j => j.Enabled && j.LastRunUtc is { } t && DateTime.UtcNow - t > TimeSpan.FromHours(Math.Max(48, j.IntervalHours * 3)) && Destination(j).Path is not null).ToList();
        var bad = failing.Concat(stale).DistinctBy(j => j.Id).ToList();
        _attention.Set("backup", bad.Count == 0 ? null : new AttentionItem
        {
            Key = "backup:" + string.Join(",", bad.Select(j => j.Id + j.LastRunUtc?.Ticks)),
            Title = bad.Count == 1 ? $"“{bad[0].Name}” hasn't backed up cleanly" : $"{bad.Count} backups need a look",
            Detail = bad[0].LastResult ?? "It hasn't run in a while.",
            Level = AttentionLevel.Warning,
            Icon = Wpf.Ui.Controls.SymbolRegular.CloudArrowUp24,
            ActionText = "Open Backup & sync",
            Action = () => { Navigator.Go("backup"); return Task.CompletedTask; },
        });
    }
}
