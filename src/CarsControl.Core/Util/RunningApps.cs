using System.Diagnostics;
using CarsControl.Core.Storage;

namespace CarsControl.Core.Util;

public sealed record RunningProcess(int Pid, string Name, string Path);

/// <summary>Finds processes started from an app's install folder, so updates can offer to close the app
/// first (PowerToys' installer was killed mid-update on 2026-08-26 because PowerToys was running).</summary>
public static class RunningApps
{
    public static IReadOnlyList<RunningProcess> InFolder(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation) || DirectorySizer.IsTooBroadToMeasure(installLocation))
            return [];
        var root = Path.GetFullPath(installLocation).TrimEnd('\\') + "\\";
        var found = new List<RunningProcess>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        found.Add(new RunningProcess(p.Id, p.ProcessName, path));
                }
                catch
                {
                    // Elevated/system processes can't be inspected from your account; they aren't ours to close anyway.
                }
            }
        }
        return found;
    }

    /// <summary>Asks windows to close (like clicking X). Never force-kills: unsaved work matters more than an update.</summary>
    public static async Task<bool> CloseGracefullyAsync(IEnumerable<RunningProcess> processes, TimeSpan wait)
    {
        var candidates = processes.ToList();
        // Check before opening any handle: a mixed group must never close Salad or its WSL/miner processes.
        if (candidates.Any(p => Health.ProcessRules.Classify(p.Name, p.Path, saladRunning: true) == Health.ProcessRole.Salad)) return false;
        var handles = new List<Process>();
        foreach (var rp in candidates)
        {
            try
            {
                var p = Process.GetProcessById(rp.Pid);
                p.CloseMainWindow();
                handles.Add(p);
            }
            catch
            {
                // Already exited.
            }
        }
        using var cts = new CancellationTokenSource(wait);
        try
        {
            await Task.WhenAll(handles.Select(h => h.WaitForExitAsync(cts.Token)));
        }
        catch (OperationCanceledException)
        {
        }
        var allGone = handles.All(h => { try { return h.HasExited; } catch { return true; } });
        handles.ForEach(h => h.Dispose());
        return allGone;
    }
}
