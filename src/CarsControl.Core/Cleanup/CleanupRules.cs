using System.Diagnostics;
using CarsControl.Core.Settings;
using CarsControl.Core.Storage;
using CarsControl.Core.Uninstall;
using CarsControl.Core.Util;

namespace CarsControl.Core.Cleanup;

public sealed record CleanupEstimate(string RuleId, long Bytes, int Items, string? Note = null);

public sealed record CleanupOutcome(string RuleId, long FreedBytes, int Removed, int Skipped, string Message);

/// <summary>
/// One cleanup category. Rules are code, never data: the helper only receives rule ids and runs its own
/// copy of the definition, so nothing a client sends can widen what an elevated process deletes.
/// </summary>
public sealed class CleanupRule
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool Elevated { get; init; }

    /// <summary>Crash dumps and error reports: kept out of the default cleanup when your profile says
    /// to keep crash evidence (Settings › This PC).</summary>
    public bool CrashEvidence { get; init; }

    private readonly bool _defaultOn = true;
    private readonly string? _warning;

    public bool DefaultOn
    {
        get => _defaultOn && !(CrashEvidence && ActiveProfile.Current.KeepCrashEvidence);
        init => _defaultOn = value;
    }

    public string? Warning
    {
        get => CrashEvidence && ActiveProfile.Current.KeepCrashEvidence
            ? $"Kept by default: {ActiveProfile.Current.KeepCrashEvidenceReason ?? "you chose to keep crash evidence"}."
            : _warning;
        init => _warning = value;
    }

    /// <summary>Process names that must not be running (their caches are locked or in use).</summary>
    public string[] BlockedBy { get; init; } = [];

    /// <summary>For rules where the exact items matter (shortcuts), the list shown before running.</summary>
    public Func<IReadOnlyList<string>>? ListItems { get; init; }

    internal Func<IEnumerable<CleanupTarget>>? Targets { get; init; }
    internal Func<CleanupEstimate>? CustomEstimate { get; init; }
    internal Func<Action<string>, CancellationToken, Task<CleanupOutcome>>? CustomRun { get; init; }
}

/// <summary>A folder whose contents (optionally only files older than MinAge / matching Pattern) may go.</summary>
internal sealed record CleanupTarget(string Root, TimeSpan MinAge, string Pattern = "*", bool Recursive = true);

public static class CleanupRules
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static IReadOnlyList<CleanupRule> All { get; } =
    [
        // ---------------- as you ----------------
        new()
        {
            Id = "user-temp", Title = "Temporary files",
            Description = "Your %TEMP% folder: files older than a day (installers, extracted archives, app scratch files).",
            Targets = () => [new CleanupTarget(Path.GetTempPath(), TimeSpan.FromDays(1))],
        },
        new()
        {
            Id = "browser-cache", Title = "Browser caches",
            Description = "Chrome, Edge and Zen web caches. Pages load a little slower the first time afterwards. Skipped while the browser is open.",
            BlockedBy = ["chrome", "msedge", "zen"],
            Targets = () => BrowserCaches(),
        },
        new()
        {
            Id = "dev-caches", Title = "npm and pip download caches",
            Description = "Package download caches; they refill on the next install.",
            Targets = () => [new CleanupTarget(Path.Combine(Local, "npm-cache"), TimeSpan.Zero), new CleanupTarget(Path.Combine(Local, "pip", "Cache"), TimeSpan.Zero)],
        },
        new()
        {
            Id = "installer-logs", Title = "winget and UniGetUI caches",
            Description = "winget diagnostic logs older than a week, plus UniGetUI's cached icons and metadata (rebuilt if it's still installed).",
            Targets = () =>
            [
                new CleanupTarget(Path.Combine(Local, @"Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\DiagOutputDir"), TimeSpan.FromDays(7)),
                new CleanupTarget(Path.Combine(Local, @"UniGetUI\CachedMedia"), TimeSpan.Zero),
                new CleanupTarget(Path.Combine(Local, @"UniGetUI\CachedMetadata"), TimeSpan.Zero),
                new CleanupTarget(Path.Combine(Local, @"CarsControl\logs"), TimeSpan.FromDays(30)),
            ],
        },
        new()
        {
            Id = "shader-cache", Title = "GPU shader caches", DefaultOn = false,
            Description = "NVIDIA/AMD/DirectX compiled shaders. Games rebuild them, which can stutter the first time you play afterwards.",
            Targets = () =>
            [
                new CleanupTarget(Path.Combine(Local, @"NVIDIA\DXCache"), TimeSpan.Zero),
                new CleanupTarget(Path.Combine(Local, @"NVIDIA\GLCache"), TimeSpan.Zero),
                new CleanupTarget(Path.Combine(Local, "D3DSCache"), TimeSpan.Zero),
                new CleanupTarget(Path.Combine(Local, @"AMD\DxCache"), TimeSpan.Zero),
                new CleanupTarget(Path.Combine(Local, @"AMD\DxcCache"), TimeSpan.Zero),
            ],
        },
        new()
        {
            Id = "app-crash-dumps", Title = "App crash dumps", CrashEvidence = true, DefaultOn = false,
            Warning = "These help diagnose app crashes; only clear them once you don't need them.",
            Description = "User-mode crash dumps in %LOCALAPPDATA%\\CrashDumps.",
            Targets = () => [new CleanupTarget(Path.Combine(Local, "CrashDumps"), TimeSpan.Zero)],
        },
        new()
        {
            Id = "broken-shortcuts", Title = "Broken shortcuts", DefaultOn = false,
            Description = "Shortcuts on the Desktop (top level only — your subfolders are left alone) and in the Start menu whose target no longer exists. Sent to the Recycle Bin. Shortcuts to unplugged drives are kept.",
            ListItems = () => BrokenShortcuts().Select(l => $"{Path.GetFileNameWithoutExtension(l)}  →  {Shortcuts.ResolveTarget(l)}").ToList(),
            CustomEstimate = () =>
            {
                var list = BrokenShortcuts();
                return new CleanupEstimate("broken-shortcuts", list.Sum(f => new FileInfo(f).Length), list.Count);
            },
            CustomRun = (log, _) =>
            {
                var list = BrokenShortcuts();
                foreach (var l in list) log($"recycling {l}");
                var ok = RecycleBin.Recycle(list);
                return Task.FromResult(new CleanupOutcome("broken-shortcuts", 0, ok ? list.Count : 0, ok ? 0 : list.Count, ok ? $"{list.Count} shortcut(s) sent to the Recycle Bin" : "Couldn't recycle them"));
            },
        },
        new()
        {
            Id = "recycle-bin", Title = "Empty the Recycle Bin", DefaultOn = false,
            Warning = "Permanent — check it first.",
            Description = "Everything in the Recycle Bin on all drives. This is the only way recycled files on A: actually free space.",
            CustomEstimate = () => { var (b, n) = RecycleBin.Query(); return new CleanupEstimate("recycle-bin", b, (int)n); },
            CustomRun = (log, _) =>
            {
                var (b, n) = RecycleBin.Query();
                var ok = RecycleBin.Empty();
                return Task.FromResult(new CleanupOutcome("recycle-bin", ok ? b : 0, ok ? (int)n : 0, 0, ok ? "Recycle Bin emptied" : "Couldn't empty the Recycle Bin"));
            },
        },

        // ---------------- admin (helper) ----------------
        new()
        {
            Id = "windows-temp", Title = "Windows temporary files", Elevated = true,
            Description = "C:\\Windows\\Temp: files older than a day.",
            Targets = () => [new CleanupTarget(Path.Combine(Windows, "Temp"), TimeSpan.FromDays(1))],
        },
        new()
        {
            Id = "delivery-optimization", Title = "Delivery Optimization cache", Elevated = true,
            Description = "Windows Update's peer-to-peer download cache.",
            Targets = () => [new CleanupTarget(Path.Combine(Windows, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), TimeSpan.Zero)],
        },
        new()
        {
            Id = "component-cleanup", Title = "Windows Update cleanup (component store)", Elevated = true, DefaultOn = false,
            Description = "DISM /StartComponentCleanup removes superseded Windows components. Safe, but can take 10+ minutes.",
            CustomEstimate = () => new CleanupEstimate("component-cleanup", 0, 0, "Size known only after it runs"),
            CustomRun = async (log, ct) =>
            {
                var r = await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "Dism.exe"), ["/Online", "/Cleanup-Image", "/StartComponentCleanup"], log, TimeSpan.FromMinutes(60), ct);
                return new CleanupOutcome("component-cleanup", 0, 0, 0, r.ExitCode == 0 ? "Component store cleaned" : $"DISM exited with {r.ExitCode}");
            },
        },
        new()
        {
            Id = "error-reports", Title = "Windows error reports", Elevated = true, CrashEvidence = true,
            Description = "Queued and archived Windows Error Reporting files in ProgramData.",
            Targets = () =>
            [
                new CleanupTarget(Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue"), TimeSpan.Zero),
                new CleanupTarget(Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"), TimeSpan.Zero),
            ],
        },
        new()
        {
            Id = "minidumps", Title = "System crash dumps (BSOD)", Elevated = true, CrashEvidence = true, DefaultOn = false,
            Warning = "Crash dumps help diagnose blue screens; only clear them once you don't need them.",
            Description = "C:\\Windows\\Minidump and MEMORY.DMP.",
            Targets = () => [new CleanupTarget(Path.Combine(Windows, "Minidump"), TimeSpan.Zero), new CleanupTarget(Windows, TimeSpan.Zero, "MEMORY.DMP", Recursive: false)],
        },
    ];

    public static CleanupRule? Find(string id) => All.FirstOrDefault(r => r.Id == id);

    /// <summary>"zen" → "Zen Browser" ("Close zen first" read like a typo).</summary>
    internal static string FriendlyProcess(string process) => process.ToLowerInvariant() switch
    {
        "chrome" => "Chrome",
        "msedge" => "Edge",
        "zen" => "Zen Browser",
        "firefox" => "Firefox",
        "brave" => "Brave",
        "opera" => "Opera",
        "steam" => "Steam",
        _ => process,
    };

    // ---------- estimate / run ----------

    public static CleanupEstimate Estimate(CleanupRule rule)
    {
        if (rule.CustomEstimate is not null) return rule.CustomEstimate();
        long bytes = 0;
        var items = 0;
        foreach (var t in rule.Targets!())
            foreach (var f in Matching(t))
            {
                bytes += f.Length;
                items++;
            }
        var blocked = Running(rule.BlockedBy);
        return new CleanupEstimate(rule.Id, bytes, items, blocked.Count > 0 ? $"Close {string.Join(" and ", blocked.Select(FriendlyProcess))} first" : null);
    }

    public static async Task<CleanupOutcome> RunAsync(CleanupRule rule, Action<string> log, bool dryRun, CancellationToken ct)
    {
        var blocked = Running(rule.BlockedBy);
        if (blocked.Count > 0) return new CleanupOutcome(rule.Id, 0, 0, 0, $"Skipped — {string.Join(", ", blocked)} is running");
        if (dryRun)
        {
            var e = Estimate(rule);
            return new CleanupOutcome(rule.Id, 0, 0, 0, $"Would free {e.Bytes / 1048576.0:F0} MB from {e.Items} item(s)");
        }
        if (rule.CustomRun is not null) return await rule.CustomRun(log, ct);

        long freed = 0;
        int removed = 0, skipped = 0;
        foreach (var t in rule.Targets!())
        {
            foreach (var f in Matching(t))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var len = f.Length;
                    f.Attributes = FileAttributes.Normal; // read-only cache files
                    f.Delete();
                    freed += len;
                    removed++;
                }
                catch
                {
                    skipped++; // in use or protected: leave it
                }
            }
            if (t.Recursive) RemoveEmptyDirectories(t.Root, t.MinAge);
        }
        return new CleanupOutcome(rule.Id, freed, removed, skipped,
            $"Freed {freed / 1048576.0:F0} MB ({removed} files)" + (skipped > 0 ? $", {skipped} in use and left alone" : ""));
    }

    /// <summary>Files in the target that may be deleted. Never follows junctions/symlinks.</summary>
    private static IEnumerable<FileInfo> Matching(CleanupTarget t)
    {
        if (!Directory.Exists(t.Root)) yield break;
        if (File.GetAttributes(t.Root).HasFlag(FileAttributes.ReparsePoint)) yield break;
        var cutoff = DateTime.UtcNow - t.MinAge;
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(t.Root).EnumerateFiles(t.Pattern, new EnumerationOptions
            {
                RecurseSubdirectories = t.Recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            }).ToList();
        }
        catch
        {
            yield break;
        }
        foreach (var f in files)
            if (t.MinAge == TimeSpan.Zero || f.LastWriteTimeUtc < cutoff) yield return f;
    }

    private static void RemoveEmptyDirectories(string root, TimeSpan minAge)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any() && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > minAge)
                        Directory.Delete(d);
                }
                catch
                {
                    // In use.
                }
            }
        }
        catch
        {
            // Root vanished.
        }
    }

    private static List<string> Running(string[] names) =>
        names.Where(n => Process.GetProcessesByName(n).Length > 0).ToList();

    private static IEnumerable<CleanupTarget> BrowserCaches()
    {
        foreach (var (vendor, sub) in new[] { (@"Google\Chrome", ""), (@"Microsoft\Edge", "") })
        {
            var userData = Path.Combine(Local, vendor, "User Data");
            if (!Directory.Exists(userData)) continue;
            foreach (var profile in Directory.EnumerateDirectories(userData))
            {
                foreach (var cache in new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache" })
                {
                    var p = Path.Combine(profile, cache);
                    if (Directory.Exists(p)) yield return new CleanupTarget(p, TimeSpan.Zero);
                }
            }
        }
        var zen = Path.Combine(Local, @"zen\Profiles");
        if (Directory.Exists(zen))
            foreach (var profile in Directory.EnumerateDirectories(zen))
                if (Directory.Exists(Path.Combine(profile, "cache2"))) yield return new CleanupTarget(Path.Combine(profile, "cache2"), TimeSpan.Zero);
    }

    /// <summary>Shortcuts with a missing target, excluding targets on drives that are simply not connected
    /// (the E: Steam library) and shell targets with no file path.</summary>
    internal static List<string> BrokenShortcuts()
    {
        // Desktop: top level only — subfolders there are deliberate (e.g. a "_BrokenShortcuts_Backup" archive).
        var roots = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.Programs), true),
        };
        var ready = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name.ToUpperInvariant()).ToHashSet();
        var broken = new List<string>();
        foreach (var (root, recurse) in roots.Where(r => Directory.Exists(r.Item1)))
        {
            foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true, MaxRecursionDepth = 3 }))
            {
                var target = Shortcuts.ResolveTarget(lnk);
                if (target is null || target.Length < 3 || !Path.IsPathFullyQualified(target)) continue;
                if (!ready.Contains(Path.GetPathRoot(target)!.ToUpperInvariant())) continue;
                if (!File.Exists(target) && !Directory.Exists(target)) broken.Add(lnk);
            }
        }
        return broken;
    }
}
