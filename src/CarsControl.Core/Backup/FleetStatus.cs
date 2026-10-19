using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CarsControl.Core.Backup;

/// <summary>
/// What one PC tells the others: a short, non-personal status. No user names, file names or paths. The Mac version writes
/// the same file (macos/Sources/CarsCore/Backup/Fleet.swift): <see cref="Windows"/> then holds "macOS 27.0.1",
/// <see cref="BlueScreensLastWeek"/> its kernel panics, and <see cref="Platform"/> is "macos". shared/fixtures/fleet has an
/// example of each that both test suites read.
/// </summary>
public sealed record FleetStatus(
    string Device,
    string AppVersion,
    DateTime AtUtc,
    string Windows,
    int UpdatesWaiting,
    long FreeBytes,
    long TotalBytes,
    int HealthWarnings,
    int BlueScreensLastWeek,
    DateTime? LastMaintenanceUtc,
    DateTime? LastBackupUtc,
    string? LastBackupResult,
    double UptimeHours,
    string? Platform = "windows")
{
    [JsonIgnore]
    public bool IsMac => string.Equals(Platform, "macos", StringComparison.OrdinalIgnoreCase) || Windows.StartsWith("macOS", StringComparison.OrdinalIgnoreCase);
}

public sealed record FleetEntry(FleetStatus Status, bool Stale, bool IsThisPc)
{
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Status.UpdatesWaiting > 0) parts.Add($"{Status.UpdatesWaiting} update{(Status.UpdatesWaiting == 1 ? "" : "s")} waiting");
            if (Status.HealthWarnings > 0) parts.Add($"{Status.HealthWarnings} health warning{(Status.HealthWarnings == 1 ? "" : "s")}");
            var crash = Status.IsMac ? "kernel panic" : "blue screen";
            if (Status.BlueScreensLastWeek > 0) parts.Add($"{Status.BlueScreensLastWeek} {crash}{(Status.BlueScreensLastWeek == 1 ? "" : "s")} this week");
            return parts.Count == 0 ? "All good" : string.Join(", ", parts);
        }
    }
}

/// <summary>
/// The "Your PCs" view: every C.A.R.S. writes <c>status-&lt;PC&gt;.json</c> into the sync folder and reads the others. The
/// cloud client (Proton Drive) moves the files; C.A.R.S. only reads and writes ordinary files in that folder.
/// </summary>
public static class FleetFiles
{
    public const string Prefix = "status-";
    public const long MaxBytes = 64_000;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string FileNameFor(string device)
    {
        var safe = Regex.Replace(device, @"[^A-Za-z0-9._-]+", "-").Trim('-', '.');
        return Prefix + (safe.Length == 0 ? "pc" : safe.Length > 40 ? safe[..40] : safe) + ".json";
    }

    public static async Task WriteAsync(string folder, FleetStatus status, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileNameFor(status.Device));
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(status, Json), ct);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>All the PCs in the folder, this one first, then the rest by name. Unreadable or oversized files are skipped.</summary>
    public static async Task<IReadOnlyList<FleetEntry>> ReadAllAsync(string folder, string thisDevice, DateTime nowUtc, CancellationToken ct = default)
    {
        var list = new List<FleetEntry>();
        if (!Directory.Exists(folder)) return list;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, Prefix + "*.json").Take(60))
            {
                try
                {
                    if (new FileInfo(file).Length > MaxBytes) continue;
                    var s = JsonSerializer.Deserialize<FleetStatus>(await File.ReadAllTextAsync(file, ct), Json);
                    if (s is null || string.IsNullOrWhiteSpace(s.Device)) continue;
                    list.Add(new FleetEntry(s, nowUtc - s.AtUtc > StaleAfter, s.Device.Equals(thisDevice, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // A file that's still being synced, or not ours.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Folder went away.
        }
        return list.OrderByDescending(e => e.IsThisPc).ThenBy(e => e.Status.Device, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
