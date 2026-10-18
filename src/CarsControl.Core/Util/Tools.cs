using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CarsControl.Core.Util;

/// <summary>Fixed locations of the system tools the app and helper call. Nothing here comes from a request.</summary>
public static partial class Tools
{
    public static string System32 => Environment.SystemDirectory;
    public static string WindowsPowerShell => Path.Combine(System32, @"WindowsPowerShell\v1.0\powershell.exe");
    public static string Wsl => Path.Combine(System32, "wsl.exe");
    public static string MsiExec => Path.Combine(System32, "msiexec.exe");
    public static string Cmd => Path.Combine(System32, "cmd.exe");
    public static string PowerCfg => Path.Combine(System32, "powercfg.exe");

    public static string OfficeC2RClient =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), @"microsoft shared\ClickToRun\OfficeC2RClient.exe");

    /// <summary>Highest machine-wide Python registered under HKLM (Python 3.14 in Program Files here).</summary>
    public static string? MachinePython()
    {
        using var core = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Python\PythonCore");
        if (core is null) return null;
        var best = core.GetSubKeyNames()
            .Select(v => (v, ver: Version.TryParse(Regex.Replace(v, @"[^\d.]", ""), out var p) ? p : new Version(0, 0)))
            .OrderByDescending(x => x.ver)
            .Select(x =>
            {
                using var ip = core.OpenSubKey($@"{x.v}\InstallPath");
                return ip?.GetValue("ExecutablePath") as string;
            })
            .FirstOrDefault(p => p is not null && File.Exists(p));
        return best;
    }

    public static string? Npm()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"nodejs\npm.cmd"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>pwsh 7 is MSIX here; its app execution alias lives in the user's WindowsApps folder.</summary>
    public static string? Pwsh()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\pwsh.exe");
        if (File.Exists(alias)) return alias;
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe");
        return File.Exists(pf) ? pf : null;
    }

    /// <summary>Package names for pip/npm/PowerShell: conservative, so they can't smuggle flags or shell syntax.</summary>
    public static bool IsSafePackageName(string name) => name.Length is > 0 and <= 120 && PackageName().IsMatch(name);

    [GeneratedRegex(@"^(@[a-z0-9][a-z0-9._-]*/)?[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex PackageName();
}
