using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CarsControl.Core.Uninstall;

public sealed record QuarantinedItem(LeftoverKind Kind, string OriginalPath, string StoredAs);

public sealed record QuarantineManifest(string Id, DateTime Created, string AppName, List<QuarantinedItem> Items);

/// <summary>
/// Leftovers are moved, not deleted: folders/shortcuts go to {root}\{id}\files, registry keys are
/// exported with reg.exe and then removed. Everything can be put back from History for 30 days.
/// User leftovers use %LOCALAPPDATA%\CarsControl\Quarantine; machine leftovers use the helper's
/// secured ProgramData\CarsControl\Quarantine.
/// </summary>
public static partial class Quarantine
{
    public static string UserRoot => Path.Combine(Util.UserData.Root, "Quarantine");

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    public static QuarantineManifest Move(string root, string appName, IReadOnlyList<LeftoverCandidate> items, Action<string> log)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0, 0xFFFFFF):x6}";
        var dir = Path.Combine(root, id);
        var files = Directory.CreateDirectory(Path.Combine(dir, "files")).FullName;
        var reg = Directory.CreateDirectory(Path.Combine(dir, "reg")).FullName;
        var manifest = new QuarantineManifest(id, DateTime.Now, appName, []);

        var n = 0;
        foreach (var item in items)
        {
            n++;
            try
            {
                switch (item.Kind)
                {
                    case LeftoverKind.Folder:
                        var folderDest = Path.Combine(files, $"{n}-{Path.GetFileName(item.Path)}");
                        MoveDirectory(item.Path, folderDest);
                        manifest.Items.Add(new QuarantinedItem(item.Kind, item.Path, folderDest));
                        log($"moved folder {item.Path}");
                        break;
                    case LeftoverKind.Shortcut:
                        var fileDest = Path.Combine(files, $"{n}-{Path.GetFileName(item.Path)}");
                        File.Move(item.Path, fileDest);
                        manifest.Items.Add(new QuarantinedItem(item.Kind, item.Path, fileDest));
                        log($"moved shortcut {item.Path}");
                        break;
                    case LeftoverKind.RegistryKey:
                        var regFile = Path.Combine(reg, $"{n}.reg");
                        ExportAndDeleteKey(item.Path, regFile);
                        manifest.Items.Add(new QuarantinedItem(item.Kind, item.Path, regFile));
                        log($"exported and removed {item.Path}");
                        break;
                }
            }
            catch (Exception ex)
            {
                log($"skipped {item.Path}: {ex.Message}");
            }
        }

        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return manifest;
    }

    public static QuarantineManifest Restore(string root, string id, Action<string> log)
    {
        if (!IsValidId(id)) throw new ArgumentException("Invalid quarantine id");
        var dir = Path.Combine(root, id);
        var manifest = JsonSerializer.Deserialize<QuarantineManifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")))
                       ?? throw new InvalidOperationException("Quarantine manifest is unreadable");

        foreach (var item in manifest.Items)
        {
            // StoredAs must live inside this quarantine folder; never act on a tampered manifest.
            if (!Path.GetFullPath(item.StoredAs).StartsWith(Path.GetFullPath(dir) + "\\", StringComparison.OrdinalIgnoreCase))
            {
                log($"refused {item.StoredAs}: outside the quarantine folder");
                continue;
            }
            try
            {
                switch (item.Kind)
                {
                    case LeftoverKind.Folder when !Directory.Exists(item.OriginalPath):
                        Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
                        MoveDirectory(item.StoredAs, item.OriginalPath);
                        log($"restored {item.OriginalPath}");
                        break;
                    case LeftoverKind.Shortcut when !File.Exists(item.OriginalPath):
                        File.Move(item.StoredAs, item.OriginalPath);
                        log($"restored {item.OriginalPath}");
                        break;
                    case LeftoverKind.RegistryKey:
                        var (_, view) = ParseKey(item.OriginalPath);
                        RunReg(["import", item.StoredAs, view == RegistryView.Registry32 ? "/reg:32" : "/reg:64"]);
                        log($"re-imported {item.OriginalPath}");
                        break;
                    default:
                        log($"left alone {item.OriginalPath}: something already exists there");
                        break;
                }
            }
            catch (Exception ex)
            {
                log($"couldn't restore {item.OriginalPath}: {ex.Message}");
            }
        }
        Directory.Delete(dir, recursive: true);
        return manifest;
    }

    /// <summary>Removes quarantines older than the retention period.</summary>
    public static int Purge(string root, TimeSpan retention)
    {
        if (!Directory.Exists(root)) return 0;
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!IsValidId(Path.GetFileName(dir))) continue;
            if (DateTime.Now - Directory.GetCreationTime(dir) < retention) continue;
            try { Directory.Delete(dir, recursive: true); removed++; } catch { /* in use; next time */ }
        }
        return removed;
    }

    internal static (RegistryKey Base, RegistryView View) ParseKey(string path)
    {
        var parts = path.Split('\\', 2);
        return parts[0] switch
        {
            "HKCU" => (RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64), RegistryView.Registry64),
            "HKLM" => (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), RegistryView.Registry64),
            "HKLM32" => (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), RegistryView.Registry32),
            _ => throw new ArgumentException($"Unsupported hive in {path}"),
        };
    }

    private static void ExportAndDeleteKey(string path, string regFile)
    {
        var (baseKey, view) = ParseKey(path);
        var sub = path.Split('\\', 2)[1];
        var regPath = (path.StartsWith("HKCU") ? "HKCU\\" : "HKLM\\") + sub;
        RunReg(["export", regPath, regFile, "/y", view == RegistryView.Registry32 ? "/reg:32" : "/reg:64"]);
        if (!File.Exists(regFile)) throw new IOException("reg export produced no file; key left in place");
        using (baseKey) baseKey.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
    }

    private static void RunReg(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe")) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(60_000);
        if (p.ExitCode != 0) throw new IOException($"reg.exe failed: {p.StandardError.ReadToEnd().Trim()}");
    }

    private static void MoveDirectory(string from, string to)
    {
        try
        {
            Directory.Move(from, to);
        }
        catch (IOException) when (!string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
        {
            CopyDirectory(from, to);
            Directory.Delete(from, recursive: true);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from))
        {
            if (File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint)) continue;
            CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }

    [GeneratedRegex(@"^\d{8}-\d{6}-[0-9a-f]{6}$")]
    private static partial Regex IdPattern();
}
