using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CarsControl.Core.Backup;

public sealed record BackupOptions(IReadOnlyList<string> Sources, string Destination, int KeepDays = 30, IReadOnlyList<string>? Excludes = null, DateTime? Now = null);

public sealed record BackupProgress(string Source, long FilesDone, long BytesDone, string? Current);

public sealed record BackupResult(long Copied, long Updated, long Archived, long Skipped, long Unchanged, long Errors, long Bytes, IReadOnlyList<string> Problems, TimeSpan Took, bool Cancelled, long PurgedVersions)
{
    public bool Ok => Errors == 0 && !Cancelled;

    public string Summary =>
        Cancelled ? "Stopped before it finished."
        : $"{Copied + Updated:N0} file{(Copied + Updated == 1 ? "" : "s")} copied ({Util.ByteSize.Format(Bytes)}), {Unchanged:N0} already there"
          + (Archived > 0 ? $", {Archived:N0} older or removed version{(Archived == 1 ? "" : "s")} kept" : "")
          + (Skipped > 0 ? $", {Skipped:N0} skipped (online-only or excluded)" : "")
          + (Errors > 0 ? $", {Errors:N0} couldn't be copied" : "") + ".";
}

/// <summary>
/// Copies folders to a backup destination, only what changed. Nothing at the destination is ever deleted by a normal
/// run: a file you changed or removed is moved to <c>.cars-versions\yyyyMMdd-HHmmss\…</c> first, and only version
/// folders older than the keep-days setting are removed. Online-only cloud files are skipped (they aren't downloaded), a
/// file another program has open is reported instead of retried forever, and the destination can't be inside a source or
/// the other way round.
/// </summary>
public static class BackupEngine
{
    public const string VersionsFolder = ".cars-versions";
    private const string TempSuffix = ".cars-tmp";
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2); // FAT and exFAT keep time to 2 s

    private static readonly FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x400000 /* RecallOnDataAccess */ | (FileAttributes)0x40000 /* RecallOnOpen */;

    /// <summary>Why this plan can't run, or null when it can.</summary>
    public static string? Validate(BackupOptions o)
    {
        if (o.Sources.Count == 0) return "Choose at least one folder to back up.";
        if (string.IsNullOrWhiteSpace(o.Destination) || !Path.IsPathFullyQualified(o.Destination)) return "Choose where the backup goes.";
        var dest = Full(o.Destination);
        var root = Path.GetPathRoot(dest);
        if (root is null || !Directory.Exists(root)) return $"{root ?? o.Destination} isn't connected.";
        if (dest.Equals(root, StringComparison.OrdinalIgnoreCase)) return "Pick a folder on that drive, not its top level.";
        if (HasLinkedParent(dest)) return "Choose a backup destination without symbolic links or junctions.";
        if (Directory.Exists(dest) && ContainsLinks(dest)) return "The backup destination contains a symbolic link or junction; remove that link before backing up.";

        var windows = Full(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var programs = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Select(Full).ToList();
        foreach (var raw in o.Sources)
        {
            if (!Path.IsPathFullyQualified(raw)) return $"“{raw}” isn't a full folder path.";
            var s = Full(raw);
            if (!Directory.Exists(s)) return $"“{s}” isn't there.";
            if (HasLinkedParent(s)) return "Choose the original source folder rather than a symbolic link or junction.";
            if (s.Equals(Path.GetPathRoot(s), StringComparison.OrdinalIgnoreCase)) return "Back up folders, not a whole drive.";
            if (IsInside(s, windows) || programs.Any(p => IsInside(s, p))) return $"“{s}” is part of Windows or its programs; that's not something to back up this way.";
            if (IsInside(dest, s)) return $"The backup folder is inside “{s}”, so it would back itself up.";
            if (IsInside(s, dest)) return $"“{s}” is inside the backup folder.";
        }
        return null;
    }

    public static Task<BackupResult> RunAsync(BackupOptions o, IProgress<BackupProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Run(o, progress, ct));

    private static BackupResult Run(BackupOptions o, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        if (Validate(o) is { } why) throw new InvalidOperationException(why);
        var sw = Stopwatch.StartNew();
        var now = o.Now ?? DateTime.Now;
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dest = Full(o.Destination);
        Directory.CreateDirectory(dest);
        var excludes = (o.Excludes ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();

        long copied = 0, updated = 0, archived = 0, skipped = 0, unchanged = 0, errors = 0, bytes = 0;
        var problems = new List<string>();
        var cancelled = false;

        var labels = Labels(o.Sources);
        foreach (var (source, label) in labels)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            var sourceRoot = Full(source);
            var targetRoot = Path.Combine(dest, label);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long done = 0, doneBytes = 0;
            var scannedAll = true;

            foreach (var file in Files(sourceRoot, excludes, dir =>
            {
                scannedAll = false;
                errors++;
                if (problems.Count < 50) problems.Add($"{dir}: couldn't read this folder; existing backup files were kept.");
            }))
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                var rel = Path.GetRelativePath(sourceRoot, file.FullName);
                seen.Add(rel);
                try
                {
                    if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || (file.Attributes & CloudOnly) != 0) { skipped++; continue; }
                    var target = Path.Combine(targetRoot, rel);
                    var existing = new FileInfo(target);
                    if (existing.Exists)
                    {
                        if (existing.Length == file.Length && (file.LastWriteTimeUtc - existing.LastWriteTimeUtc).Duration() <= TimeTolerance) { unchanged++; continue; }
                        MoveToVersions(dest, stamp, label, rel, existing);
                        archived++;
                        Copy(file, target);
                        updated++;
                    }
                    else
                    {
                        Copy(file, target);
                        copied++;
                    }
                    bytes += file.Length;
                    doneBytes += file.Length;
                    done++;
                    if (done % 25 == 0) progress?.Report(new BackupProgress(label, done, doneBytes, rel));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors++;
                    if (problems.Count < 50) problems.Add($"{label}\\{rel}: {Reason(ex)}");
                    if (ex is IOException io && IsDiskFull(io)) { problems.Add("The backup drive is full."); cancelled = true; break; }
                }
            }
            if (cancelled) break;

            // What was removed (or renamed) at the source is kept as a version, not deleted from the backup.
            if (scannedAll && Directory.Exists(targetRoot))
            {
                foreach (var old in new DirectoryInfo(targetRoot).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                    if (old.Name.EndsWith(TempSuffix, StringComparison.Ordinal)) { TryDelete(old); continue; }
                    var rel = Path.GetRelativePath(targetRoot, old.FullName);
                    if (seen.Contains(rel) || Excluded(rel, excludes)) continue;
                    try
                    {
                        MoveToVersions(dest, stamp, label, rel, old);
                        archived++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        errors++;
                        if (problems.Count < 50) problems.Add($"{label}\\{rel}: {Reason(ex)}");
                    }
                }
            }
            progress?.Report(new BackupProgress(label, done, doneBytes, null));
        }

        var purged = cancelled ? 0 : PurgeVersions(dest, now, o.KeepDays);
        return new BackupResult(copied, updated, archived, skipped, unchanged, errors, bytes, problems, sw.Elapsed, cancelled, purged);
    }

    // ---------- pieces ----------

    /// <summary>"Documents", "Pictures"; two folders with the same name get " (2)".</summary>
    public static IReadOnlyList<(string Source, string Label)> Labels(IReadOnlyList<string> sources)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string)>();
        foreach (var s in sources.Select(Full).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = new string(Path.GetFileName(s).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
            if (name.Length == 0) name = "Folder";
            var label = name;
            for (var n = 2; !used.Add(label); n++) label = $"{name} ({n})";
            list.Add((s, label));
        }
        return list;
    }

    private static IEnumerable<FileInfo> Files(string root, IReadOnlyList<string> excludes, Action<string> unreadable)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.System, ReturnSpecialDirectories = false };
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<DirectoryInfo> subs;
            IEnumerable<FileInfo> files;
            try
            {
                subs = dir.EnumerateDirectories("*", options).ToList();
                files = dir.EnumerateFiles("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable(dir.FullName);
                continue;
            }
            foreach (var sub in subs)
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // junctions and symlinks: the real folder is backed up where it lives
                if (Excluded(sub.Name, excludes)) continue;
                stack.Push(sub);
            }
            foreach (var f in files)
            {
                if (Excluded(f.Name, excludes) || f.Name.EndsWith(TempSuffix, StringComparison.Ordinal)) continue;
                yield return f;
            }
        }
    }

    /// <summary>Simple patterns: "*.tmp", "node_modules", "Thumbs.db". Matches a name or any folder name on the way.</summary>
    public static bool Excluded(string relativeOrName, IReadOnlyList<string> excludes)
    {
        if (excludes.Count == 0) return false;
        foreach (var part in relativeOrName.Split('\\', '/'))
            foreach (var pattern in excludes)
                if (Wild(pattern).IsMatch(part)) return true;
        return false;
    }

    private static Regex Wild(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void Copy(FileInfo source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + TempSuffix;
        try
        {
            // FileShare.ReadWrite: copy a file another program keeps open for reading (not one it holds exclusively).
            using (var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                input.CopyTo(output, 1 << 20);
            File.SetLastWriteTimeUtc(tmp, source.LastWriteTimeUtc);
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            TryDelete(new FileInfo(tmp));
            throw;
        }
    }

    private static void MoveToVersions(string dest, string stamp, string label, string rel, FileInfo existing)
    {
        var versioned = Path.Combine(dest, VersionsFolder, stamp, label, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(versioned)!);
        File.Move(existing.FullName, versioned, overwrite: true);
    }

    /// <summary>Removes version folders older than <paramref name="keepDays"/>. Only folders under .cars-versions named like a timestamp.</summary>
    public static long PurgeVersions(string dest, DateTime now, int keepDays)
    {
        var folder = Path.Combine(dest, VersionsFolder);
        if (!Directory.Exists(folder) || keepDays < 1) return 0;
        // A manually placed junction must never redirect retention deletion outside this backup.
        if (HasLinkedParent(folder) || ContainsLinks(folder)) return 0;
        long purged = 0;
        foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories())
        {
            if (!DateTime.TryParseExact(dir.Name, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) continue;
            if (now - when < TimeSpan.FromDays(keepDays)) continue;
            try
            {
                dir.Delete(recursive: true);
                purged++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Next run tries again.
            }
        }
        return purged;
    }

    private static bool HasLinkedParent(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.LinkTarget is not null) return true;
        return false;
    }

    private static bool ContainsLinks(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out var dir))
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null) return true;
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        return false;
    }

    private static bool IsInside(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string Full(string path) => Path.GetFullPath(path).TrimEnd('\\');

    private static void TryDelete(FileInfo f)
    {
        try { if (f.Exists) f.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next time */ }
    }

    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 112 or 39; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL

    private static string Reason(Exception ex) => ex is UnauthorizedAccessException ? "no permission" : (ex.HResult & 0xFFFF) is 32 or 33 ? "another program has it open" : IsDiskFull((IOException)ex) ? "the drive is full" : ex.Message;
}
