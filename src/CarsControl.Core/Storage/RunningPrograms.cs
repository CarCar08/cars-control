using System.Diagnostics;
using CarsControl.Core.Elevation;

namespace CarsControl.Core.Storage;

/// <summary>Whether programs are running, by name or from inside a folder (a game being open, Steam being open).</summary>
public static class RunningPrograms
{
    public static bool IsRunning(string processName)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            p.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>True when any program's file lives under <paramref name="folder"/>.</summary>
    public static bool AnyUnder(string folder)
    {
        var prefix = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (Native.ImagePath(p.Id) is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited, or not ours to look at.
                }
            }
        }
        return false;
    }
}
