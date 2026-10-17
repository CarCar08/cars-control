using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using CarsControl.Contracts;
using CarsControl.Core.Storage;
using CarsControl.Core.Util;

namespace CarsControl.Core.Elevation;

/// <summary>
/// Fast drive scans in the helper: WizTree (fixed path in Program Files) reads the NTFS master file table,
/// which needs admin and takes seconds even on an HDD. The export lands in ProgramData\CarsControl\scans,
/// whose ACL is SYSTEM/Administrators + the parent user (you) read-only: the listing includes other
/// accounts' file names, so it is not left readable by every user.
/// </summary>
internal static class StorageOps
{
    public static bool IsValidDrive(string drive) =>
        drive.Length == 1 && char.IsAsciiLetter(drive[0]);

    public static async Task<OpResult> ScanAsync(StorageScanOp op, bool dryRun, Action<string> line, HelperLog log, string? parentSid, CancellationToken ct)
    {
        if (!IsValidDrive(op.Drive)) return OpResult.Reject("Invalid drive letter.");
        var letter = char.ToUpperInvariant(op.Drive[0]);
        var drive = new DriveInfo(letter.ToString());
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return OpResult.Reject($"{letter}: is not a fixed drive.");
        if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) return OpResult.Reject($"{letter}: is {drive.DriveFormat}; fast scan needs NTFS.");
        if (!WizTreeImport.IsInstalled) return new OpResult(OpStatus.Failed, -1, "WizTree is not installed.");

        var args = new[] { $@"{letter}:\", $"/export={{0}}", "/admin=1" };
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, HelperOperations.Describe(WizTreeImport.ExePath, [args[0], "/export=<scans>\\" + letter + "-….csv", args[2]]));

        var dir = ScansDirectory(parentSid);
        foreach (var old in Directory.EnumerateFiles(dir, "*.csv")) TryDelete(old);
        var csv = Path.Combine(dir, $"{letter}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        var sw = Stopwatch.StartNew();
        line($"Reading the {letter}: master file table with WizTree…");
        var r = await ProcessRunner.RunAsync(WizTreeImport.ExePath, [args[0], $"/export={csv}", args[2]], line, TimeSpan.FromMinutes(15), ct);
        log.Write($"wiztree {letter}: exit {r.ExitCode} in {sw.ElapsedMilliseconds} ms, csv exists={File.Exists(csv)}");
        if (!File.Exists(csv)) return new OpResult(OpStatus.Failed, r.ExitCode, "WizTree didn't produce an export.");
        return new OpResult(OpStatus.Succeeded, 0, $"Scanned {letter}: in {sw.Elapsed.TotalSeconds:F1}s", Data: csv);
    }

    /// <summary>Deletes leftover exports (called when the helper exits).</summary>
    public static void CleanupExports()
    {
        try
        {
            var dir = Path.Combine(SecureDataDir.Root, "scans");
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.csv")) TryDelete(f);
        }
        catch
        {
            // Best effort.
        }
    }

    private static string ScansDirectory(string? parentSid) => SecureDataDir.EnsurePrivate("scans", parentSid);

    private static void TryDelete(string f)
    {
        try { File.Delete(f); } catch { /* in use: next time */ }
    }
}
