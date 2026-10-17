using CarsControl.Contracts;
using CarsControl.Core.Storage;
using CarsControl.Core.Util;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CarsControl.Core.Elevation;

/// <summary>What must be true before the helper deletes the old folder of a moved Epic game. Pure, so it's tested directly.</summary>
public static class MovedGameRules
{
    public static bool IsValidManifestId(string? id) => id is not null && Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{1,120}\z");

    /// <summary>Why the old folder can't be removed, or null. <paramref name="manifestLocations"/> are the InstallLocations in Epic's .item manifests.</summary>
    public static string? Validate(string oldFolder, string newFolder, IEnumerable<string> manifestLocations, Func<string, bool> folderExists, Func<string, bool> isLink)
    {
        if (!IsLocalPath(oldFolder) || !IsLocalPath(newFolder))
            return "Those aren't full folder paths.";
        var old = Path.GetFullPath(oldFolder).TrimEnd('\\');
        var now = Path.GetFullPath(newFolder).TrimEnd('\\');
        if (old.Equals(now, StringComparison.OrdinalIgnoreCase)) return "The old and new folders are the same.";
        var root = Path.GetPathRoot(old)!;
        var parent = Path.GetDirectoryName(old);
        if (!new[] { Path.Combine(root, "Epic Games"), Path.Combine(root, "Program Files", "Epic Games"), Path.Combine(root, "Program Files (x86)", "Epic Games") }
            .Contains(parent, StringComparer.OrdinalIgnoreCase)) return "Only a game directly inside a standard Epic Games library can be removed this way.";
        if (Path.GetPathRoot(now)!.Equals(root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(now), Path.Combine(Path.GetPathRoot(now)!, "Epic Games"), StringComparison.OrdinalIgnoreCase))
            return "The new copy must be in Epic Games on another drive.";
        if (!Path.GetFileName(old).Equals(Path.GetFileName(now), StringComparison.OrdinalIgnoreCase)) return "The new folder isn't the same game.";
        if (old.Contains("Salad", StringComparison.OrdinalIgnoreCase)) return "Salad is protected and cannot be changed.";
        if (!folderExists(old)) return "The old folder isn't there any more.";
        if (!folderExists(now)) return "The new folder isn't there, so the old one is the only copy.";
        if (isLink(old) || isLink(now)) return "A game folder or its parent contains links.";
        var locations = manifestLocations.Select(l => l.Replace('/', '\\').TrimEnd('\\')).ToList();
        if (!locations.Contains(now, StringComparer.OrdinalIgnoreCase)) return "The Epic launcher doesn't point at the new folder yet.";
        if (locations.Any(l => l.Equals(old, StringComparison.OrdinalIgnoreCase) || l.StartsWith(old + "\\", StringComparison.OrdinalIgnoreCase)))
            return "The Epic launcher still points at the old folder.";
        return null;
    }

    private static bool IsLocalPath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length >= 4
        && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
        && !path[2..].Any(c => c < 32 || "<>:\"|?*/".Contains(c))
        && path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries).All(p => !p.EndsWith('.') && !p.EndsWith(' '));
}

/// <summary>Deletes the old copy of a moved game after <see cref="MovedGameRules"/> and a file-by-file comparison pass.</summary>
internal static class GameMoveOps
{
    public static async Task<OpResult> RemoveOldAsync(RemoveMovedGameOp op, bool dryRun, Action<string> line, HelperLog log, CancellationToken ct)
    {
        if (!MovedGameRules.IsValidManifestId(op.ManifestId)) return OpResult.Reject("Invalid Epic manifest identifier.");
        try { return await RemoveResolvedAsync(op, dryRun, line, log, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return OpResult.Reject($"The old copy couldn't be removed safely: {ex.Message}");
        }
    }

    private static async Task<OpResult> RemoveResolvedAsync(RemoveMovedGameOp op, bool dryRun, Action<string> line, HelperLog log, CancellationToken ct)
    {
        // The app supplies only a manifest id. Resolve both locations ourselves from Epic's fixed directory.
        var item = Path.Combine(EpicMover.ManifestsFolder, op.ManifestId + ".item");
        string oldFolder, newFolder;
        try
        {
            if (EpicMover.HasLinks(EpicMover.DataRoot) || new FileInfo(item).Attributes.HasFlag(FileAttributes.ReparsePoint)
                || new FileInfo(item + ".cars-before-move").Attributes.HasFlag(FileAttributes.ReparsePoint))
                return OpResult.Reject("Epic's records contain links.");
            oldFolder = JsonNode.Parse(File.ReadAllText(item + ".cars-before-move"))?["InstallLocation"]?.GetValue<string>() ?? "";
            newFolder = JsonNode.Parse(File.ReadAllText(item))?["InstallLocation"]?.GetValue<string>() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return OpResult.Reject($"Epic's move records couldn't be read: {ex.Message}");
        }
        var locations = ManifestLocations(oldFolder, newFolder);
        if (MovedGameRules.Validate(oldFolder, newFolder, locations, Directory.Exists, EpicMover.HasLinks) is { } why) return OpResult.Reject(why);
        var old = Path.GetFullPath(oldFolder).TrimEnd('\\');
        var now = Path.GetFullPath(newFolder).TrimEnd('\\');
        if (EpicMover.LauncherRunning() || RunningPrograms.AnyUnder(old) || RunningPrograms.AnyUnder(now))
            return OpResult.Reject("Close the Epic Games Launcher and the game first.");
        if (EpicMover.VerifyCopy(old, now, ct, targetMayHaveMore: true) is { } diff) return OpResult.Reject($"The two copies differ, so the old one stays: {diff}");
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, $"Would delete {old} (the game now lives in {now}).");
        ct.ThrowIfCancellationRequested();

        line("Removing the old copy…");
        var empty = Path.Combine(Path.GetTempPath(), "cars-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            // An empty mirror deletes everything (long paths included) without following junctions; exit codes under 8 mean it worked.
            var robocopy = Path.Combine(Tools.System32, "robocopy.exe");
            var r = await ProcessRunner.RunAsync(robocopy, [empty, old, "/MIR", "/R:0", "/W:0", "/NFL", "/NDL", "/NJH", "/NJS", "/NP", "/XJ"], null, TimeSpan.FromHours(1), ct);
            if (r.ExitCode >= 8) return new OpResult(OpStatus.Failed, r.ExitCode, "Windows wouldn't delete everything. Some files may be in use.");
            Directory.Delete(old, recursive: false);
            if (Directory.Exists(old)) return new OpResult(OpStatus.Failed, -1, "The old copy is still there.");
        }
        finally
        {
            try { Directory.Delete(empty, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
        }
        log.Write($"moved game: removed old copy {old} (now {now})");
        return OpResult.Success($"Removed the old copy at {old}.");
    }

    private static List<string> ManifestLocations(string oldFolder, string newFolder)
    {
        var list = new List<string>();
        if (!Directory.Exists(EpicMover.ManifestsFolder)) return list;
        foreach (var file in Directory.EnumerateFiles(EpicMover.ManifestsFolder, "*.item"))
        {
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))?["InstallLocation"]?.GetValue<string>() is { } loc) list.Add(loc);
            }
            // Fail closed: an unreadable record may still point at the old copy.
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
            { throw new IOException("An Epic manifest couldn't be checked; the old copy must stay.", ex); }
        }
        var installed = JsonNode.Parse(File.ReadAllText(EpicMover.LauncherInstalledFile));
        if (installed?["InstallationList"] is not JsonArray entries) throw new IOException("Epic's installation list couldn't be checked.");
        var installedLocations = entries.Select(entry => entry?["InstallLocation"]?.GetValue<string>()?.Replace('/', '\\').TrimEnd('\\')).ToList();
        // Launchers with install-helper (.egi) records leave this legacy list empty; then only "still lists the old folder" matters.
        var eoshInUse = Directory.Exists(EpicMover.EoshItemsFolder) && Directory.EnumerateFiles(EpicMover.EoshItemsFolder, "*.egi").Any();
        if ((!eoshInUse && !installedLocations.Contains(newFolder.Replace('/', '\\').TrimEnd('\\'), StringComparer.OrdinalIgnoreCase))
            || installedLocations.Contains(oldFolder.Replace('/', '\\').TrimEnd('\\'), StringComparer.OrdinalIgnoreCase))
            throw new IOException("Epic's installation list hasn't switched to the new copy.");
        list.AddRange(installedLocations.OfType<string>());
        // Newer launchers rebuild the two records above from Epic Online Services' install helper: it must agree too.
        if (Directory.Exists(EpicMover.EoshItemsFolder))
            foreach (var file in Directory.EnumerateFiles(EpicMover.EoshItemsFolder, "*.egi"))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(file))?["v4"]?["dir"]?.GetValue<string>() is { } dir) list.Add(dir);
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
                { throw new IOException("An Epic install-helper record couldn't be checked; the old copy must stay.", ex); }
            }
        return list;
    }
}
