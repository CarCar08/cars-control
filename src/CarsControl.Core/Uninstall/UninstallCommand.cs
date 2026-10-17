using System.Text;
using System.Text.RegularExpressions;

namespace CarsControl.Core.Uninstall;

public enum InstallerKind { Unknown, Msi, Inno, Nsis, Squirrel, InstallShield, Msix }

/// <summary>An uninstall string split into executable + arguments.</summary>
public sealed record CommandLine(string Exe, string Args)
{
    public override string ToString() => string.IsNullOrEmpty(Args) ? $"\"{Exe}\"" : $"\"{Exe}\" {Args}";
}

public static partial class UninstallCommand
{
    /// <summary>Splits an ARP uninstall string. Handles quoted paths and the common unquoted
    /// "C:\Program Files\App\uninst.exe /x" form by finding the shortest prefix ending in .exe.</summary>
    public static CommandLine? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = Environment.ExpandEnvironmentVariables(raw.Trim());

        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end < 0) return null;
            return new CommandLine(s[1..end], s[(end + 1)..].Trim());
        }

        var m = ExeToken().Match(s);
        if (m.Success)
        {
            var exe = s[..(m.Index + m.Length)];
            return new CommandLine(exe.Trim(), s[(m.Index + m.Length)..].Trim());
        }
        var space = s.IndexOf(' ');
        return space < 0 ? new CommandLine(s, "") : new CommandLine(s[..space], s[(space + 1)..].Trim());
    }

    /// <summary>Guesses the installer technology from the uninstaller's file name and contents.</summary>
    public static InstallerKind Detect(CommandLine cmd)
    {
        var file = Path.GetFileName(cmd.Exe);
        if (file.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)) return InstallerKind.Msi;
        if (InnoName().IsMatch(file)) return InstallerKind.Inno;
        if (file.Equals("Update.exe", StringComparison.OrdinalIgnoreCase) && cmd.Args.Contains("--uninstall", StringComparison.OrdinalIgnoreCase)) return InstallerKind.Squirrel;
        if (cmd.Args.Contains("-runfromtemp", StringComparison.OrdinalIgnoreCase) || cmd.Exe.Contains("InstallShield", StringComparison.OrdinalIgnoreCase)) return InstallerKind.InstallShield;
        if (FileContains(cmd.Exe, "Nullsoft")) return InstallerKind.Nsis;
        if (FileContains(cmd.Exe, "Inno Setup")) return InstallerKind.Inno;
        return InstallerKind.Unknown;
    }

    /// <summary>Adds the silent switches each installer family understands. Null when unknown.</summary>
    public static CommandLine? MakeSilent(CommandLine cmd, InstallerKind kind) => kind switch
    {
        InstallerKind.Inno => cmd with { Args = Append(cmd.Args, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") },
        InstallerKind.Nsis => cmd with { Args = Append(cmd.Args, "/S") },
        InstallerKind.Squirrel => cmd with { Args = Append(cmd.Args, "-s") },
        _ => null,
    };

    private static string Append(string args, string extra) =>
        args.Contains(extra.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? args : $"{args} {extra}".Trim();

    /// <summary>Looks for a marker string in the first 2 MB (NSIS/Inno stubs carry their name there).</summary>
    private static bool FileContains(string path, string marker)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            var buf = new byte[Math.Min(fs.Length, 2 * 1024 * 1024)];
            var read = fs.Read(buf, 0, buf.Length);
            return Encoding.ASCII.GetString(buf, 0, read).Contains(marker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex(@"\.exe(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExeToken();

    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InnoName();
}
