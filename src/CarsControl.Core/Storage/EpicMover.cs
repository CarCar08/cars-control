using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace CarsControl.Core.Storage;

public sealed record EpicMovePlan(GameEntry Game, string ItemFile, string From, string To, long Bytes, string? Blocker, string? EoshFile = null, int EarlierCopyExtras = 0)
{
    public bool CanRun => Blocker is null;
}

/// <summary>
/// Moves one Epic Games Store game to another drive, the way the launcher's own "move" would if it had one: the game folder is
/// copied and checked, then every launcher record of where it lives is switched to the new place: the game's .item manifest,
/// LauncherInstalled.dat and, in launchers that use Epic Online Services' install helper (2026+), its .egi record, which the launcher
/// treats as the truth and rebuilds the other two from. The old copy is kept until the owner has started the game from its new
/// place (<see cref="OldCopyOf"/>): if Epic ever undoes the move, nothing has to be downloaded again.
/// </summary>
public static class EpicMover
{
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic");
    public static string ManifestsFolder => Path.Combine(DataRoot, @"EpicGamesLauncher\Data\Manifests");
    public static string LauncherInstalledFile => Path.Combine(DataRoot, @"UnrealEngineLauncher\LauncherInstalled.dat");
    public static string EoshItemsFolder => Path.Combine(DataRoot, @"EpicOnlineServicesShared\InstallHelper\InstalledItems");

    /// <summary>Every Epic process that keeps install records in memory and may write them back.</summary>
    public static readonly string[] LauncherProcesses = ["EpicGamesLauncher", "EpicWebHelper", "EpicOnlineServicesHost", "EpicOnlineServicesUserHelper"];

    public static bool LauncherRunning() => LauncherProcesses.Any(RunningPrograms.IsRunning);

    /// <summary>The install helper's record of the same installation (same id as the .item), or null if this launcher has none.</summary>
    public static string? EoshRecordFor(string itemFile, string? eoshFolder = null)
    {
        var file = Path.Combine(eoshFolder ?? EoshItemsFolder, Path.GetFileNameWithoutExtension(itemFile) + ".egi");
        return File.Exists(file) ? file : null;
    }

    private static string? EoshProblem(string itemFile, string from, string? eoshFolder)
    {
        if (EoshRecordFor(itemFile, eoshFolder) is not { } file) return null;
        try
        {
            RewriteEosh(File.ReadAllText(file), from, from);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return $"Epic's install helper record for this game couldn't be updated safely ({ex.Message}), so nothing was changed.";
        }
    }

    /// <summary>The install helper's .egi record pointing at the new folder. Refuses a format it doesn't know rather than guessing.</summary>
    public static string RewriteEosh(string json, string from, string to)
    {
        var node = JsonNode.Parse(json) ?? throw new JsonException("empty install record");
        if (node["v4"] is not JsonObject v4 || v4["dir"]?.GetValue<string>() is not { } dir)
            throw new JsonException("Epic's install helper keeps this game's record in a format C.A.R.S. doesn't know.");
        if (!SamePath(dir, from)) throw new JsonException("Epic's install helper no longer points at the original game folder.");
        foreach (var key in new[] { "dir", "metaDir", "manifestPath", "pendingManifestPath" })
            if (v4[key]?.GetValue<string>() is { Length: > 0 } value) v4[key] = Repoint(value, from, to);
        return node.ToJsonString();
    }

    /// <summary>
    /// After a move, while both copies exist: the old folder (from the .cars-before-move backup) and the new one (from the
    /// .cars-moved-to marker). <c>Reverted</c> is true when any Epic record points at the old folder again: Epic undid the move,
    /// so the new copy is the unused one. Null when there's no pending move, or either copy is gone.
    /// </summary>
    public static (string Old, string New, bool Reverted)? OldCopyOf(string itemFile, string? eoshFolder = null)
    {
        try
        {
            var backup = itemFile + ".cars-before-move";
            var marker = itemFile + ".cars-moved-to";
            if (!File.Exists(backup) || !File.Exists(marker) || !File.Exists(itemFile)) return null;
            var old = JsonNode.Parse(File.ReadAllText(backup))?["InstallLocation"]?.GetValue<string>();
            var target = File.ReadAllText(marker).Trim();
            var now = JsonNode.Parse(File.ReadAllText(itemFile))?["InstallLocation"]?.GetValue<string>();
            if (old is null || now is null || target.Length == 0 || SamePath(old, target) || !Directory.Exists(old) || !Directory.Exists(target)) return null;
            var eosh = EoshRecordFor(itemFile, eoshFolder);
            var eoshDir = eosh is null ? null : JsonNode.Parse(File.ReadAllText(eosh))?["v4"]?["dir"]?.GetValue<string>();
            var reverted = SamePath(now, old) || (eoshDir is not null && SamePath(eoshDir, old));
            if (!reverted && !SamePath(now, target)) return null; // the game has been moved somewhere else since
            return (old, target, reverted);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The game's .item manifest: the one whose InstallLocation is the game's folder.</summary>
    public static string? FindItemFile(string installPath, string? folder = null)
    {
        folder ??= ManifestsFolder;
        if (!Directory.Exists(folder)) return null;
        string? match = null;
        foreach (var file in Directory.EnumerateFiles(folder, "*.item"))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file))?["InstallLocation"]?.GetValue<string>() is { } loc && SamePath(loc, installPath))
                {
                    if (match is not null) return null; // Ambiguous records must not leave a second game pointing at a removed folder.
                    match = file;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                // A manifest that can't be read isn't this game's.
            }
        }
        return match;
    }

    /// <summary>Where a game lands on a drive: an "Epic Games" folder at its root, like the launcher's default.</summary>
    public static string TargetFor(string drive, string installPath) =>
        Path.Combine(Path.GetPathRoot(drive) ?? drive, "Epic Games", Path.GetFileName(installPath.Replace('/', '\\').TrimEnd('\\')));

    public static EpicMovePlan Plan(GameEntry game, string targetDrive, Func<bool> launcherRunning, Func<string, bool> gameRunning, string? manifestsFolder = null, string? eoshFolder = null)
    {
        string? blocker = null;
        var from = game.InstallPath ?? "";
        var to = from.Length > 0 ? TargetFor(targetDrive, from) : "";
        var item = "";
        string? eosh = null;
        long bytes = 0;

        if (!game.Source.Equals("Epic", StringComparison.OrdinalIgnoreCase)) blocker = "Only Epic games can be moved this way.";
        else if (game.Name.Contains("Salad", StringComparison.OrdinalIgnoreCase) || from.Contains("Salad", StringComparison.OrdinalIgnoreCase)) blocker = "Salad is protected and cannot be changed.";
        else if (from.Length == 0 || !Path.IsPathFullyQualified(from) || !Directory.Exists(from)) blocker = "The game's folder isn't there or isn't an absolute path.";
        else if (SamePath(Path.GetPathRoot(from)!, Path.GetPathRoot(targetDrive)!)) blocker = "It's already on that drive.";
        else if (FindItemFile(from, manifestsFolder) is not { } found) blocker = "One unique Epic launcher record of this game wasn't found, so it couldn't be told where the game went.";
        else if (HasLinks(from) || HasLinks(to)) blocker = "The game's folder or destination contains links (junctions), which can't be copied safely.";
        else if (EoshProblem(found, from, eoshFolder) is { } eoshProblem) blocker = eoshProblem;
        else if (launcherRunning()) blocker = "Close the Epic Games Launcher first (also from the tray), and wait a few seconds for its background services to stop: they keep these records in memory.";
        else if (gameRunning(from)) blocker = "The game is running. Close it first.";
        else
        {
            item = found;
            eosh = EoshRecordFor(found, eoshFolder);
            bytes = TreeOps.Measure(from).Bytes;
            if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any() && TreeOps.ExtrasInEarlierCopy(to, from) is null)
                blocker = $"{to} already exists and holds something else.";
            else
            {
                var free = new DriveInfo(Path.GetPathRoot(targetDrive)!).AvailableFreeSpace;
                var needed = RequiredFreeBytes(from, to, bytes);
                if (free < needed) blocker = $"Not enough room: finishing the copy needs about {Util.ByteSize.Format(needed)} free (including a safety margin); the drive has {Util.ByteSize.Format(free)} free.";
            }
        }
        var earlierExtras = blocker is null && Directory.Exists(to) ? TreeOps.ExtrasInEarlierCopy(to, from)?.Count ?? 0 : 0;
        return new EpicMovePlan(game, item, from, to, bytes, blocker, eosh, earlierExtras);
    }

    /// <summary>The .item manifest with every path that pointed into the old folder pointing into the new one; other fields untouched.</summary>
    public static string RewriteItem(string json, string from, string to)
    {
        var node = JsonNode.Parse(json) ?? throw new JsonException("empty manifest");
        if (node["InstallLocation"]?.GetValue<string>() is not { } location || !SamePath(location, from))
            throw new JsonException("The Epic manifest no longer points at the original game folder.");
        foreach (var key in new[] { "InstallLocation", "ManifestLocation", "StagingLocation" })
            if (node[key]?.GetValue<string>() is { } value) node[key] = Repoint(value, from, to);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>LauncherInstalled.dat with this game's entry pointing at the new folder.</summary>
    /// <param name="mayBeAbsent">True for launchers that keep Epic Online Services' install-helper records: they leave this legacy
    /// list empty (seen on the 2026 launcher), so a missing entry is returned unchanged. Two entries are always refused.</param>
    public static string RewriteLauncherInstalled(string json, string from, string to, bool mayBeAbsent = false)
    {
        if (mayBeAbsent)
        {
            var parsed = JsonNode.Parse(json) ?? throw new JsonException("empty list");
            if (parsed["InstallationList"] is JsonArray entries
                && !entries.Any(e => e?["InstallLocation"]?.GetValue<string>() is { } l && SamePath(l, from)))
                return json;
        }
        var node = JsonNode.Parse(json) ?? throw new JsonException("empty list");
        var matches = 0;
        if (node["InstallationList"] is JsonArray list)
            foreach (var entry in list)
                if (entry?["InstallLocation"]?.GetValue<string>() is { } loc && SamePath(loc, from))
                {
                    entry["InstallLocation"] = to;
                    matches++;
                }
        if (matches != 1) throw new JsonException("The Epic installation list must contain exactly one record of this game.");
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Runs the move: copy, verify, then switch every launcher record. The old copy is kept: <see cref="RemoveOldCopyAsync"/> removes it
    /// once the owner has started the game from its new place.
    /// </summary>
    public static Task<MoveResult> RunAsync(EpicMovePlan plan, IProgress<MoveProgress>? progress, CancellationToken ct) =>
        RunAsync(plan, progress, ct, LauncherInstalledFile);

    internal static Task<MoveResult> RunAsync(EpicMovePlan plan, IProgress<MoveProgress>? progress, CancellationToken ct, string installedFile, Func<bool>? launcherRunning = null) =>
        Task.Run(async () =>
        {
            if (!plan.CanRun) return new MoveResult(false, plan.Blocker!);
            long filesDone = 0, bytesDone = 0;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (plan.From.Contains("Salad", StringComparison.OrdinalIgnoreCase)) return new MoveResult(false, "Salad is protected and cannot be changed.");
                if (SamePath(plan.From, plan.To) || plan.To.StartsWith(plan.From.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                    || plan.From.StartsWith(plan.To.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    return new MoveResult(false, "The game folders must be separate.");
                if (Busy(plan.From, launcherRunning)) return new MoveResult(false, "Close the Epic Games Launcher and the game first.");
                if (HasLinks(plan.From) || HasLinks(plan.To)) return new MoveResult(false, "The game or destination contains links; nothing was changed.");
                if (Directory.Exists(plan.To))
                {
                    // An earlier copy of this game is reused; only the files the game created there are removed first.
                    if (TreeOps.ExtrasInEarlierCopy(plan.To, plan.From) is not { } extras)
                        return new MoveResult(false, "The destination holds something else; nothing was changed.");
                    RemoveExtras(plan.To, plan.From, extras);
                }
                var (filesTotal, _) = TreeOps.Measure(plan.From);
                RepairPartialCopy(plan.From, plan.To, progress, ct);
                TreeOps.Copy(plan.From, plan.To, progress, "Copying the game", filesTotal, plan.Bytes, ref filesDone, ref bytesDone, ct);
                progress?.Report(new MoveProgress("Checking the copy", filesDone, filesTotal, bytesDone, plan.Bytes));
                if (VerifyCopy(plan.From, plan.To, ct, progress) is { } problem) return new MoveResult(false, $"The original stays where it was. {problem}");
                ct.ThrowIfCancellationRequested();
                if (Busy(plan.From, launcherRunning)) return new MoveResult(false, "The launcher or game was opened during the copy. Close it and try again; the original stays where it was.");

                // Prepare every record before writing any. Once switching starts, finish or restore all of them without cancellation.
                progress?.Report(new MoveProgress("Telling the launcher where the game is now", filesDone, filesTotal, bytesDone, plan.Bytes));
                var records = new List<(string File, string Before, string After)>();
                var item = await File.ReadAllTextAsync(plan.ItemFile, ct);
                records.Add((plan.ItemFile, item, RewriteItem(item, plan.From, plan.To)));
                var installed = await File.ReadAllTextAsync(installedFile, ct);
                var newInstalled = RewriteLauncherInstalled(installed, plan.From, plan.To, mayBeAbsent: plan.EoshFile is not null);
                if (!ReferenceEquals(newInstalled, installed)) records.Add((installedFile, installed, newInstalled)); // the 2026 launcher leaves it empty
                if (plan.EoshFile is { } eoshFile)
                {
                    var eosh = await File.ReadAllTextAsync(eoshFile, ct);
                    records.Add((eoshFile, eosh, RewriteEosh(eosh, plan.From, plan.To)));
                }
                foreach (var (file, before, _) in records) AtomicWrite(file + ".cars-before-move", before);
                AtomicWrite(plan.ItemFile + ".cars-moved-to", plan.To);
                ct.ThrowIfCancellationRequested();
                var written = new List<(string File, string Before)>();
                try
                {
                    foreach (var (file, before, after) in records)
                    {
                        AtomicWrite(file, after);
                        written.Add((file, before));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    foreach (var (file, before) in Enumerable.Reverse(written))
                    {
                        try { AtomicWrite(file, before); }
                        catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                        {
                            return new MoveResult(false, $"Both game copies are safe, but Epic's records need restoring from their .cars-before-move backups: {rollback.Message}");
                        }
                    }
                    throw;
                }

                return new MoveResult(true,
                    $"Copied {plan.Game.Name} to {plan.To} and pointed Epic at it. Start the Epic Games Launcher and play it once. " +
                    $"If it starts without downloading, use “Remove old copy” on the Games tab to free {Util.ByteSize.Format(plan.Bytes)} at {plan.From}. " +
                    "Until then the old copy stays, so nothing has to be downloaded again if Epic doesn't accept the move.", plan.Bytes);
            }
            catch (OperationCanceledException)
            {
                return new MoveResult(false, "Stopped. The game is still where it was; what was already copied is reused next time.", Cancelled: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return new MoveResult(false, $"The move stopped: {ex.Message}. The game is still where it was.");
            }
        });

    /// <summary>
    /// Deletes the old copy after a move, once Epic still points at the new one (<see cref="OldCopyOf"/>) and both copies match.
    /// <paramref name="removeOld"/> asks the admin helper when this account can't delete (Program Files); it returns why not, or null.
    /// </summary>
    public static Task<string?> RemoveOldCopyAsync(string itemFile, Func<string, Task<string?>> removeOld, CancellationToken ct) =>
        RemoveOldCopyAsync(itemFile, removeOld, ct, LauncherRunning, null);

    internal static Task<string?> RemoveOldCopyAsync(string itemFile, Func<string, Task<string?>> removeOld, CancellationToken ct, Func<bool> launcherRunning, string? eoshFolder) =>
        Task.Run(async () =>
        {
            if (OldCopyOf(itemFile, eoshFolder) is not { } state) return "There's no old copy of this game to remove.";
            if (state.Reverted) return await RemoveUnusedNewCopyAsync(itemFile, state.Old, state.New, launcherRunning, eoshFolder, ct);
            if (launcherRunning() || RunningPrograms.AnyUnder(state.Old) || RunningPrograms.AnyUnder(state.New)) return "Close the Epic Games Launcher and the game first.";
            if (HasLinks(state.Old)) return "The old folder contains links; it was left alone.";
            if (VerifyCopy(state.Old, state.New, ct, targetMayHaveMore: true) is { } diff) return $"The two copies differ, so the old one stays: {diff}";
            // Program Files only lets administrators delete: hand those to the helper before touching anything, so a half-deleted
            // folder never fails the helper's own "both copies match" check.
            if (NeedsAdminToDelete(state.Old)) return await removeOld(Path.GetFileNameWithoutExtension(itemFile));
            try
            {
                TreeOps.DeleteTree(state.Old);
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                return $"Windows wouldn't let this account delete everything ({ex.Message}).";
            }
            catch (IOException ex)
            {
                return $"Some files couldn't be removed ({ex.Message}).";
            }
        }, ct);

    /// <summary>Epic undid the move and uses the old folder again: the copy C.A.R.S. made is unused and can go.</summary>
    private static Task<string?> RemoveUnusedNewCopyAsync(string itemFile, string old, string copy, Func<bool> launcherRunning, string? eoshFolder, CancellationToken ct)
    {
        if (launcherRunning() || RunningPrograms.AnyUnder(copy)) return Task.FromResult<string?>("Close the Epic Games Launcher and the game first.");
        try
        {
            // Every record must point at the old folder, and the copy must be the one this app made (X:\Epic Games\<same folder>).
            var item = JsonNode.Parse(File.ReadAllText(itemFile))?["InstallLocation"]?.GetValue<string>();
            var eosh = EoshRecordFor(itemFile, eoshFolder) is { } e ? JsonNode.Parse(File.ReadAllText(e))?["v4"]?["dir"]?.GetValue<string>() : old;
            if (item is null || eosh is null || !SamePath(item, old) || !SamePath(eosh, old))
                return Task.FromResult<string?>("Epic's records don't all point at the old folder, so both copies stay.");
            if (!SamePath(Path.GetDirectoryName(copy) ?? "", Path.Combine(Path.GetPathRoot(copy) ?? "", "Epic Games"))
                || !string.Equals(Path.GetFileName(copy.TrimEnd('\\')), Path.GetFileName(old.TrimEnd('\\')), StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<string?>("That folder isn't the copy C.A.R.S. made, so it was left alone.");
            if (HasLinks(copy)) return Task.FromResult<string?>("The unused copy contains links; it was left alone.");
            ct.ThrowIfCancellationRequested();
            TreeOps.DeleteTree(copy);
            File.Delete(itemFile + ".cars-moved-to");
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Task.FromResult<string?>($"The unused copy couldn't be removed ({ex.Message}).");
        }
    }

    private static bool NeedsAdminToDelete(string folder) =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(p => p.Length > 0)
            .Any(p => folder.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    private static bool Busy(string folder, Func<bool>? launcherRunning) => (launcherRunning ?? LauncherRunning)() || RunningPrograms.AnyUnder(folder);

    private static void RemoveExtras(string copy, string original, IReadOnlyList<string> extras)
    {
        foreach (var relative in extras)
        {
            var file = new FileInfo(Path.Combine(copy, relative));
            if (!file.FullName.StartsWith(Path.GetFullPath(copy).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Attributes.HasFlag(FileAttributes.ReadOnly)) file.Attributes &= ~FileAttributes.ReadOnly;
            file.Delete();
        }
        // Then the folders those files lived in, if the original doesn't have them and they're now empty (deepest first).
        foreach (var dir in Directory.GetDirectories(copy, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.Exists(Path.Combine(original, Path.GetRelativePath(copy, dir))) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
    }

    internal static void AtomicWrite(string file, string text)
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".cars-tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool HasLinks(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        return Directory.Exists(folder) && TreeOps.FindLinks(folder, 1).Count > 0;
    }

    /// <summary>Verify relative names and SHA-256 contents, not just totals, before discarding the only original.</summary>
    /// <summary>
    /// Null when every file of <paramref name="source"/> is in <paramref name="target"/> with the same name and SHA-256. With
    /// <paramref name="targetMayHaveMore"/>, extra files in the target are fine: a game started from its new place writes its own
    /// (crash reports, logs), and the question then is only whether the old copy holds anything the new one lacks.
    /// </summary>
    internal static string? VerifyCopy(string source, string target, CancellationToken ct, IProgress<MoveProgress>? progress = null, bool targetMayHaveMore = false)
    {
        if (!Directory.Exists(source) || !Directory.Exists(target)) return "One of the game folders isn't there.";
        if (HasLinks(source) || HasLinks(target)) return "A game folder contains links.";
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 };
        var sourceFiles = Directory.GetFiles(source, "*", options);
        var targetCount = Directory.GetFiles(target, "*", options).Length;
        if (targetMayHaveMore ? targetCount < sourceFiles.Length : targetCount != sourceFiles.Length) return "The copies contain different files.";
        var total = sourceFiles.Sum(file => new FileInfo(file).Length);
        long checkedBytes = 0, checkedFiles = 0;
        foreach (var file in sourceFiles)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var twin = Path.Combine(target, relative);
            if (!File.Exists(twin) || new FileInfo(file).Length != new FileInfo(twin).Length) return $"The copy of {relative} is missing or has a different size.";
            using var original = File.OpenRead(file);
            using var copy = File.OpenRead(twin);
            var originalHash = Hash(original, ct, read => progress?.Report(new MoveProgress("Checking the copy", checkedFiles, sourceFiles.Length, checkedBytes + read / 2, total)));
            if (!originalHash.AsSpan().SequenceEqual(Hash(copy, ct, read => progress?.Report(new MoveProgress("Checking the copy", checkedFiles, sourceFiles.Length, checkedBytes + original.Length / 2 + read / 2, total)))))
                return $"The contents of {relative} differ.";
            checkedBytes += original.Length;
            checkedFiles++;
        }
        progress?.Report(new MoveProgress("Checking the copy", checkedFiles, sourceFiles.Length, total, total));
        return null;
    }

    internal static long RequiredFreeBytes(string source, string target, long totalBytes)
    {
        long copied = 0;
        if (Directory.Exists(target))
            foreach (var file in new DirectoryInfo(source).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var twin = new FileInfo(Path.Combine(target, Path.GetRelativePath(source, file.FullName)));
                if (twin.Exists && twin.Length == file.Length) copied += file.Length;
            }
        return Math.Max(0, totalBytes - copied) + totalBytes / 20 + 512L * 1024 * 1024;
    }

    private static void RepairPartialCopy(string source, string target, IProgress<MoveProgress>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(target)) return;
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (file.EndsWith(".cars-tmp", StringComparison.Ordinal)) continue;
            var originalFile = Path.Combine(source, Path.GetRelativePath(target, file));
            progress?.Report(new MoveProgress("Checking previously copied files", 0, 0, 0, 0));
            bool matches;
            using (var original = File.OpenRead(originalFile))
            using (var copy = File.OpenRead(file)) matches = Hash(original, ct).AsSpan().SequenceEqual(Hash(copy, ct));
            if (!matches)
            {
                // Only discard a bad partial copy after checking its original; the original is never changed here.
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                File.Delete(file);
            }
            else File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(originalFile));
        }
    }

    private static byte[] Hash(Stream stream, CancellationToken ct, Action<long>? report = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var timer = System.Diagnostics.Stopwatch.StartNew();
        long done = 0;
        int read;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if ((read = stream.Read(buffer)) == 0) return hash.GetHashAndReset();
            hash.AppendData(buffer, 0, read);
            done += read;
            if (timer.ElapsedMilliseconds >= 250) { report?.Invoke(done); timer.Restart(); }
        }
    }

    private static string Repoint(string value, string from, string to)
    {
        var norm = value.Replace('/', '\\');
        var f = from.Replace('/', '\\').TrimEnd('\\');
        if (norm.TrimEnd('\\').Equals(f, StringComparison.OrdinalIgnoreCase)) return to;
        if (norm.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase))
        {
            var rest = value[(f.Length + 1)..];
            return to.TrimEnd('\\') + "\\" + rest; // keep the launcher's own separators after the folder ("\.egstore/bps")
        }
        return value;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('/', '\\').TrimEnd('\\'), b.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
