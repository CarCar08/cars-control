using CarsControl.Contracts;
using CarsControl.Core.Storage;
using CarsControl.Core.Util;
using Microsoft.Win32;

namespace CarsControl.Core.Elevation;

/// <summary>
/// Removes one top-level folder of a leftover Windows installation (see <see cref="OldWindowsRules"/> for what's allowed).
/// The folder belongs to TrustedInstaller, so it takes ownership, grants administrators access, and empties it with robocopy
/// (which copes with the very long paths and odd names old profiles have) before removing the shell of the folder.
/// </summary>
internal static class OldWindowsOps
{
    public static async Task<OpResult> ExecuteAsync(RemoveOldWindowsOp op, bool dryRun, Action<string> line, HelperLog log, CancellationToken ct)
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory)!;
        if (OldWindowsRules.Validate(op.Folder, system, SafeFileExists, SafeIsLink) is { } why) return OpResult.Reject(why);
        var folder = Path.GetFullPath(op.Folder).TrimEnd('\\');

        // A program that's still registered inside this folder would break.
        var users = OldWindowsRules.StillUsedBy(folder, RegisteredPrograms());
        if (users.Count > 0) return OpResult.Reject($"Still in use by {string.Join(", ", users)}. Uninstall them first.");

        // A junction or symbolic link on this Windows that leads into the folder would be left pointing at nothing.
        var linked = OldWindowsRules.LinkedFrom(folder, LinksOnThisSystem());
        if (linked.Count > 0) return OpResult.Reject($"{linked[0]} is a link into that folder, so it stays. Remove or repoint the link first.");

        var takeown = Path.Combine(Tools.System32, "takeown.exe");
        var icacls = Path.Combine(Tools.System32, "icacls.exe");
        var robocopy = Path.Combine(Tools.System32, "robocopy.exe");
        string[] ownArgs = ["/F", folder, "/R", "/A", "/D", "Y"];
        string[] aclArgs = [folder, "/grant", "*S-1-5-32-544:(OI)(CI)F", "/T", "/C", "/Q"];
        if (dryRun)
            return new OpResult(OpStatus.DryRun, 0, string.Join(Environment.NewLine, HelperOperations.Describe(takeown, ownArgs), HelperOperations.Describe(icacls, aclArgs),
                $"{HelperOperations.Describe(robocopy, ["<empty folder>", folder, "/MIR"])} (empties it), then remove {folder}"));

        log.Write($"old windows: removing {folder}");
        line($"Taking ownership of {Path.GetFileName(folder)}…");
        await ProcessRunner.RunAsync(takeown, ownArgs, null, TimeSpan.FromMinutes(30), ct);
        line("Granting administrators access…");
        await ProcessRunner.RunAsync(icacls, aclArgs, null, TimeSpan.FromMinutes(30), ct);

        var empty = Path.Combine(Path.GetTempPath(), "cars-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            line("Deleting the files… (this can take a while)");
            // /MIR of an empty folder deletes everything in the target; exit codes under 8 mean it worked.
            var r = await ProcessRunner.RunAsync(robocopy, [empty, folder, "/MIR", "/R:0", "/W:0", "/NFL", "/NDL", "/NJH", "/NJS", "/NP", "/XJ"], null, TimeSpan.FromHours(3), ct);
            log.Write($"robocopy exit {r.ExitCode}");
            if (r.ExitCode >= 8) return new OpResult(OpStatus.Failed, r.ExitCode, "Windows wouldn't delete everything. Some files may be in use.");
            try
            {
                Directory.Delete(folder, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new OpResult(OpStatus.Failed, -1, $"The contents are gone but the empty folder couldn't be removed: {ex.Message}");
            }
        }
        finally
        {
            try { Directory.Delete(empty, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
        }
        log.Write($"old windows: removed {folder}");
        return OpResult.Success($"Removed {folder}.");
    }

    /// <summary>The links directly inside the places a profile or program would keep one: every user folder, AppData, ProgramData, Program Files.</summary>
    private static IEnumerable<(string Link, string? Target)> LinksOnThisSystem()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory)!;
        var places = new List<string> { system, Path.Combine(system, "ProgramData"), Path.Combine(system, "Program Files"), Path.Combine(system, "Program Files (x86)") };
        try
        {
            foreach (var user in Directory.EnumerateDirectories(Path.Combine(system, "Users")))
            {
                places.Add(user);
                places.Add(Path.Combine(user, "AppData", "Local"));
                places.Add(Path.Combine(user, "AppData", "Roaming"));
                places.Add(Path.Combine(user, "Documents"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the user folders only the other places are checked.
        }
        foreach (var place in places.Where(Directory.Exists))
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(place).EnumerateFileSystemInfos().Where(e => e.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var e in entries)
            {
                string? target = null;
                try { target = e.ResolveLinkTarget(returnFinalTarget: true)?.FullName; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable link */ }
                yield return (e.FullName, target);
            }
        }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool SafeIsLink(string path)
    {
        try { return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static IEnumerable<(string Name, string? Path)> RegisteredPrograms()
    {
        foreach (var (hive, key) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            using var root = hive.OpenSubKey(key);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                var display = k?.GetValue("DisplayName") as string ?? name;
                foreach (var value in new[] { "InstallLocation", "DisplayIcon", "UninstallString", "InstallSource" })
                    if (k?.GetValue(value) is string path && path.Length > 3) yield return (display, path);
            }
        }
    }
}
