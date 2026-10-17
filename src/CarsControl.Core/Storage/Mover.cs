using System.Diagnostics;
using System.Globalization;

namespace CarsControl.Core.Storage;

public sealed record MoveProgress(string Stage, long FilesDone, long FilesTotal, long BytesDone, long BytesTotal)
{
    public double Percent => BytesTotal <= 0 ? 0 : Math.Min(100, BytesDone * 100.0 / BytesTotal);
}

public sealed record MoveResult(bool Ok, string Message, long BytesMoved = 0, bool Cancelled = false);

/// <summary>Copy-and-verify building blocks for moving a game or a folder to another drive.</summary>
public static class TreeOps
{
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    public static (long Files, long Bytes) Measure(string folder)
    {
        long files = 0, bytes = 0;
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            files++;
            bytes += f.Length;
        }
        return (files, bytes);
    }

    /// <summary>The first junctions or symbolic links inside a folder: copying them as plain folders would silently double their contents.</summary>
    public static IReadOnlyList<string> FindLinks(string folder, int max = 3)
    {
        var found = new List<string>();
        var stack = new Stack<string>();
        stack.Push(folder);
        while (stack.Count > 0 && found.Count < max)
        {
            var dir = new DirectoryInfo(stack.Pop());
            try
            {
                foreach (var d in dir.EnumerateDirectories())
                {
                    if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) found.Add(d.FullName);
                    else stack.Push(d.FullName);
                }
                foreach (var f in dir.EnumerateFiles())
                    if (f.Attributes.HasFlag(FileAttributes.ReparsePoint) && found.Count < max) found.Add(f.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: the copy will report it.
            }
        }
        return found;
    }

    /// <summary>Copies a folder tree, skipping files that already match (so an interrupted move resumes). Throws on the first error.</summary>
    public static void Copy(string source, string target, IProgress<MoveProgress>? progress, string stage, long filesTotal, long bytesTotal, ref long filesDone, ref long bytesDone, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        var root = new DirectoryInfo(source);
        var stack = new Stack<(DirectoryInfo Src, string Dst)>();
        stack.Push((root, target));
        var lastReport = Stopwatch.StartNew();
        while (stack.Count > 0)
        {
            var (src, dst) = stack.Pop();
            Directory.CreateDirectory(dst);
            foreach (var d in src.EnumerateDirectories("*", options)) stack.Push((d, Path.Combine(dst, d.Name)));
            foreach (var f in src.EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                var to = Path.Combine(dst, f.Name);
                var existing = new FileInfo(to);
                if (!(existing.Exists && existing.Length == f.Length && (existing.LastWriteTimeUtc - f.LastWriteTimeUtc).Duration() <= TimeTolerance))
                {
                    var tmp = to + ".cars-tmp";
                    f.CopyTo(tmp, overwrite: true);
                    File.SetAttributes(tmp, f.Attributes & ~FileAttributes.ReadOnly);
                    File.SetLastWriteTimeUtc(tmp, f.LastWriteTimeUtc);
                    File.SetCreationTimeUtc(tmp, f.CreationTimeUtc);
                    File.Move(tmp, to, overwrite: true);
                    if (f.Attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(to, f.Attributes);
                }
                filesDone++;
                bytesDone += f.Length;
                if (lastReport.ElapsedMilliseconds > 400)
                {
                    progress?.Report(new MoveProgress(stage, filesDone, filesTotal, bytesDone, bytesTotal));
                    lastReport.Restart();
                }
            }
        }
        progress?.Report(new MoveProgress(stage, filesDone, filesTotal, bytesDone, bytesTotal));
    }

    /// <summary>Same files, same sizes: the copy can stand in for the original.</summary>
    public static string? Differences(string source, string target)
    {
        var (sf, sb) = Measure(source);
        if (!Directory.Exists(target)) return "The copy isn't there.";
        var (tf, tb) = Measure(target);
        return sf == tf && sb == tb ? null : $"The copy has {tf:N0} files ({tb:N0} bytes) but the original has {sf:N0} ({sb:N0}).";
    }

    /// <summary>Every file in <paramref name="part"/> is also in <paramref name="whole"/> at the same size: what an interrupted copy leaves.</summary>
    /// <summary>
    /// An earlier copy of <paramref name="whole"/> that the game has since run from: nothing in it conflicts (every file it shares
    /// with the original has the same size), at least 90% of its files are the original's, and the rest are files the game made
    /// there (crash reports, logs). Returns those extra files (relative paths), or null when it isn't such a copy.
    /// A plain partial copy returns an empty list.
    /// </summary>
    public static IReadOnlyList<string>? ExtrasInEarlierCopy(string part, string whole)
    {
        try
        {
            var extras = new List<string>();
            var shared = 0;
            foreach (var f in new DirectoryInfo(part).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (f.Name.EndsWith(".cars-tmp", StringComparison.Ordinal)) continue;
                var relative = Path.GetRelativePath(part, f.FullName);
                var twin = new FileInfo(Path.Combine(whole, relative));
                if (!twin.Exists) extras.Add(relative);
                else if (twin.Length != f.Length) return null;
                else shared++;
            }
            if (extras.Count == 0) return extras;
            return shared > 0 && shared >= 9 * extras.Count ? extras : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsPartialCopyOf(string part, string whole)
    {
        try
        {
            foreach (var f in new DirectoryInfo(part).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (f.Name.EndsWith(".cars-tmp", StringComparison.Ordinal)) continue;
                var twin = new FileInfo(Path.Combine(whole, Path.GetRelativePath(part, f.FullName)));
                if (!twin.Exists || twin.Length != f.Length) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void DeleteTree(string folder)
    {
        // Read-only files (games ship some) would stop a plain delete.
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
            if (f.Attributes.HasFlag(FileAttributes.ReadOnly)) f.Attributes &= ~FileAttributes.ReadOnly;
        Directory.Delete(folder, recursive: true);
    }
}

public sealed record SteamMovePlan(
    GameEntry Game,
    string FromLibrary,
    string ToLibrary,
    IReadOnlyList<(string From, string To, bool IsFile)> Items,
    long Bytes,
    string? Blocker)
{
    public bool CanRun => Blocker is null;
}

/// <summary>
/// Moves one Steam game to another Steam library the way Steam itself does: the game's folder, its appmanifest, its Workshop
/// files and shader cache are copied and checked; the manifest is switched over last, so Steam never sees a half-moved
/// game; only then are the old copies removed. Steam must be closed (it keeps those files open) and the game not running.
/// </summary>
public static class SteamMover
{
    public static SteamMovePlan Plan(GameEntry game, string toLibrary, IReadOnlyCollection<string> knownLibraries, Func<bool> steamRunning, Func<string, bool> gameRunning)
    {
        string Blocked(string why) => why;
        string? blocker = null;
        var items = new List<(string, string, bool)>();
        long bytes = 0;

        if (!game.Source.Equals("Steam", StringComparison.OrdinalIgnoreCase) || game.SteamAppId is not { } appId || !appId.All(char.IsAsciiDigit)) blocker = "Only Steam games can be moved this way.";
        else if (game.InstallPath is not { } install || !Directory.Exists(install)) blocker = "The game's folder isn't there.";
        else if (string.Equals(game.Library.TrimEnd('\\'), toLibrary.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) blocker = "It's already in that library.";
        else if (!knownLibraries.Any(l => string.Equals(l.TrimEnd('\\'), toLibrary.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) || !Directory.Exists(Path.Combine(toLibrary, "steamapps")))
            blocker = "That isn't one of Steam's libraries. Add it in Steam › Settings › Storage first.";
        else if (new DirectoryInfo(install).Attributes.HasFlag(FileAttributes.ReparsePoint) || TreeOps.FindLinks(install, 1).Count > 0)
            blocker = "The game's folder contains links (junctions), which can't be copied safely.";
        else if (steamRunning()) blocker = "Close Steam first: it keeps these files open.";
        else if (gameRunning(install)) blocker = "The game is running. Close it first.";
        else
        {
            var fromApps = Path.Combine(game.Library, "steamapps");
            var toApps = Path.Combine(toLibrary, "steamapps");
            var dirName = Path.GetFileName(install.TrimEnd('\\'));
            if (Directory.Exists(Path.Combine(fromApps, "downloading", appId))) blocker = "Steam is part-way through downloading or updating this game. Let it finish first.";
            else
            {
                var manifest = Path.Combine(fromApps, $"appmanifest_{appId}.acf");
                if (!File.Exists(manifest)) blocker = "The game's Steam manifest is missing.";
                else
                {
                    var (_, gameBytes) = TreeOps.Measure(install);
                    bytes += gameBytes;
                    items.Add((install, Path.Combine(toApps, "common", dirName), false));
                    foreach (var rel in new[] { Path.Combine("workshop", "content", appId), Path.Combine("shadercache", appId) })
                    {
                        var dir = Path.Combine(fromApps, rel);
                        if (!Directory.Exists(dir)) continue;
                        bytes += TreeOps.Measure(dir).Bytes;
                        items.Add((dir, Path.Combine(toApps, rel), false));
                    }
                    var workshopManifest = Path.Combine(fromApps, "workshop", $"appworkshop_{appId}.acf");
                    if (File.Exists(workshopManifest)) items.Add((workshopManifest, Path.Combine(toApps, "workshop", $"appworkshop_{appId}.acf"), true));
                    items.Add((manifest, Path.Combine(toApps, $"appmanifest_{appId}.acf"), true)); // last: it's what makes Steam see the game
                    if (Directory.Exists(items[0].Item2) && Directory.EnumerateFileSystemEntries(items[0].Item2).Any() && !TreeOps.IsPartialCopyOf(items[0].Item2, install))
                        blocker = Blocked("That library already has a different folder with the game's name.");
                    else
                    {
                        var free = new DriveInfo(Path.GetPathRoot(toLibrary)!).AvailableFreeSpace;
                        if (free < bytes + bytes / 20 + 512L * 1024 * 1024) blocker = $"Not enough room: it needs about {Util.ByteSize.Format(bytes)} and the drive has {Util.ByteSize.Format(free)} free.";
                    }
                }
            }
        }
        return new SteamMovePlan(game, game.Library, toLibrary, items, bytes, blocker);
    }

    public static Task<MoveResult> RunAsync(SteamMovePlan plan, IProgress<MoveProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Run(plan, progress, ct));

    private static MoveResult Run(SteamMovePlan plan, IProgress<MoveProgress>? progress, CancellationToken ct)
    {
        if (!plan.CanRun) return new MoveResult(false, plan.Blocker!);
        long filesTotal = 0;
        foreach (var (from, _, isFile) in plan.Items) filesTotal += isFile ? 1 : TreeOps.Measure(from).Files;
        long filesDone = 0, bytesDone = 0;
        try
        {
            // 1. Folders (the long part), then 2. the small files, the Steam manifest last.
            foreach (var (from, to, isFile) in plan.Items.OrderBy(i => i.IsFile ? 1 : 0))
            {
                ct.ThrowIfCancellationRequested();
                if (isFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(from, to + ".cars-tmp", overwrite: true);
                    File.Move(to + ".cars-tmp", to, overwrite: true);
                    filesDone++;
                    bytesDone += new FileInfo(from).Length;
                }
                else
                {
                    TreeOps.Copy(from, to, progress, $"Copying {Path.GetFileName(from)}", filesTotal, plan.Bytes, ref filesDone, ref bytesDone, ct);
                }
            }

            // 3. Check every copy before anything old is touched.
            progress?.Report(new MoveProgress("Checking the copy", filesDone, filesTotal, bytesDone, plan.Bytes));
            foreach (var (from, to, isFile) in plan.Items)
            {
                var problem = isFile ? (File.Exists(to) && new FileInfo(to).Length == new FileInfo(from).Length ? null : $"{Path.GetFileName(to)} didn't copy correctly.") : TreeOps.Differences(from, to);
                if (problem is not null) return new MoveResult(false, $"Nothing was removed. {problem}");
            }

            // 4. The new library has everything: now the old copies can go, manifest first so Steam never lists it twice.
            progress?.Report(new MoveProgress("Removing the old copy", filesDone, filesTotal, bytesDone, plan.Bytes));
            foreach (var (from, _, isFile) in plan.Items.OrderByDescending(i => i.IsFile ? 1 : 0))
            {
                if (isFile) File.Delete(from);
                else TreeOps.DeleteTree(from);
            }
            return new MoveResult(true, $"Moved {plan.Game.Name} to {plan.ToLibrary}. Start Steam again and it will find the game there.", plan.Bytes);
        }
        catch (OperationCanceledException)
        {
            return new MoveResult(false, "Stopped. The game is still where it was; what was already copied is reused next time.", Cancelled: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MoveResult(false, $"The move stopped: {ex.Message}. The game is still where it was.");
        }
    }
}

public sealed record LinkedMove(string Original, string NewLocation, DateTime When, long Bytes);

/// <summary>
/// Moves a big folder to another drive and leaves a junction in its place, so everything that uses the old path keeps
/// working. The original is renamed (not deleted) until the junction is confirmed, and "move back" undoes it.
/// </summary>
public static class FolderLinkMover
{
    /// <summary>Why this folder must not be moved, or null.</summary>
    public static string? Refuse(string folder, string targetDrive)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return "Choose a full folder path.";
        var full = Path.GetFullPath(folder).TrimEnd('\\');
        if (full.Split('\\').Any(part => part.StartsWith("Salad", StringComparison.OrdinalIgnoreCase))) return "Salad is permanently protected and cannot be moved.";
        var root = Path.GetPathRoot(full)!;
        if (full.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || full.Length <= 3) return "That's a whole drive.";
        if (!Directory.Exists(full)) return "That folder isn't there.";
        var info = new DirectoryInfo(full);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "That folder is already a link to somewhere else.";
        if (targetDrive.Length < 2 || char.ToUpperInvariant(targetDrive[0]) == char.ToUpperInvariant(full[0])) return "Choose a different drive to move it to.";

        var env = Environment.GetEnvironmentVariable("SystemRoot")!;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var forbidden = new[]
        {
            env, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + @"\Microsoft", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + @"\Packages",
            profile, Path.Combine(profile, "AppData"), Path.Combine(profile, "AppData", "Local"), Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "LocalLow"),
            Path.Combine(profile, "AppData", "Local", "Packages"), Path.Combine(profile, "AppData", "Local", "Microsoft"), Path.Combine(root, "$Recycle.Bin"), Path.Combine(root, "System Volume Information"),
            Path.Combine(root, "Recovery"), Path.Combine(root, "Users"), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        foreach (var f in forbidden.Where(f => !string.IsNullOrEmpty(f)).Select(f => f.TrimEnd('\\')))
            if (full.Equals(f, StringComparison.OrdinalIgnoreCase) || (IsSystemTree(f) && full.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase))) return $"“{full}” is part of Windows or its programs: moving it could stop things from working.";
        if (full.Contains(@"\CarsControl", StringComparison.OrdinalIgnoreCase)) return "That's this app's own data.";
        foreach (var sync in CloudSync.Roots())
            if (full.StartsWith(sync.Path + "\\", StringComparison.OrdinalIgnoreCase) || full.Equals(sync.Path, StringComparison.OrdinalIgnoreCase)) return $"That's inside {sync.Provider}: its files may be online-only, and a link inside a synced folder confuses the sync.";
        return null;
    }

    // Folders whose whole tree is off limits (versus only the folder itself, like the profile root).
    private static bool IsSystemTree(string f) =>
        f.EndsWith(@"\Windows", StringComparison.OrdinalIgnoreCase) || f.Contains("Program Files", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase) || f.EndsWith(@"\Packages", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || f.EndsWith("System Volume Information", StringComparison.OrdinalIgnoreCase) || f.EndsWith("Recovery", StringComparison.OrdinalIgnoreCase);

    public static Task<MoveResult> MoveAsync(string folder, string targetFolder, IProgress<MoveProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Move(folder, targetFolder, progress, ct));

    private static MoveResult Move(string folder, string target, IProgress<MoveProgress>? progress, CancellationToken ct)
    {
        folder = Path.GetFullPath(folder).TrimEnd('\\');
        target = Path.GetFullPath(target).TrimEnd('\\');
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && !TreeOps.IsPartialCopyOf(target, folder)) return new MoveResult(false, "The target folder already has something else in it.");
        if (TreeOps.FindLinks(folder, 1).Count > 0) return new MoveResult(false, "That folder contains links (junctions), which can't be copied safely.");
        var old = folder + ".cars-old";
        if (Directory.Exists(old)) return new MoveResult(false, $"{old} is left over from an earlier move. Check it and remove it first.");
        var (files, bytes) = TreeOps.Measure(folder);
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace;
            if (free < bytes + bytes / 20 + 256L * 1024 * 1024) return new MoveResult(false, $"Not enough room on the other drive: it needs about {Util.ByteSize.Format(bytes)}.");
            long filesDone = 0, bytesDone = 0;
            TreeOps.Copy(folder, target, progress, "Copying", files, bytes, ref filesDone, ref bytesDone, ct);
            if (TreeOps.Differences(folder, target) is { } diff) return new MoveResult(false, $"Nothing was changed. {diff}");

            // The old folder becomes .cars-old; only a working junction lets it be deleted.
            try { Directory.Move(folder, old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new MoveResult(false, $"Something is using the folder, so it can't be replaced by a link ({ex.Message}). Close the program that uses it and try again. The copy on the other drive was kept."); }
            if (!MakeJunction(folder, target))
            {
                Directory.Move(old, folder);
                return new MoveResult(false, "Windows wouldn't create the link, so the folder was put back as it was.");
            }
            if (!Directory.Exists(folder) || !new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint) || TreeOps.Measure(folder).Files != files)
            {
                Directory.Delete(folder);
                Directory.Move(old, folder);
                return new MoveResult(false, "The link didn't work, so the folder was put back as it was.");
            }
            progress?.Report(new MoveProgress("Removing the old copy", filesDone, files, bytes, bytes));
            TreeOps.DeleteTree(old);
            return new MoveResult(true, $"Moved to {target}. {folder} is now a link to it, so nothing that uses it notices.", bytes);
        }
        catch (OperationCanceledException)
        {
            return new MoveResult(false, "Stopped. The folder is where it was; the part already copied is reused next time.", Cancelled: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MoveResult(false, $"The move stopped: {ex.Message}. The original folder is unchanged.");
        }
    }

    /// <summary>Undoes a move: removes the link and copies the folder back.</summary>
    public static Task<MoveResult> MoveBackAsync(LinkedMove move, IProgress<MoveProgress>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(move.NewLocation)) return new MoveResult(false, "The moved copy isn't there any more.");
                var link = new DirectoryInfo(move.Original);
                if (!link.Exists || !link.Attributes.HasFlag(FileAttributes.ReparsePoint)) return new MoveResult(false, "The original folder isn't a link any more, so there's nothing to undo.");
                var (files, bytes) = TreeOps.Measure(move.NewLocation);
                var back = move.Original + ".cars-back";
                long filesDone = 0, bytesDone = 0;
                TreeOps.Copy(move.NewLocation, back, progress, "Copying back", files, bytes, ref filesDone, ref bytesDone, ct);
                if (TreeOps.Differences(move.NewLocation, back) is { } diff) return new MoveResult(false, $"Nothing was changed. {diff}");
                Directory.Delete(move.Original); // a link: removes only the link
                Directory.Move(back, move.Original);
                TreeOps.DeleteTree(move.NewLocation);
                return new MoveResult(true, $"Moved back to {move.Original}.", bytes);
            }
            catch (OperationCanceledException)
            {
                return new MoveResult(false, "Stopped. The link is still in place.", Cancelled: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new MoveResult(false, $"Couldn't move it back: {ex.Message}");
            }
        });

    private static bool MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var p = Process.Start(psi);
        p?.WaitForExit(15000);
        return p?.ExitCode == 0;
    }
}
