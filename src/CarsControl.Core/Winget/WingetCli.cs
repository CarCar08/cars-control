using System.Text.RegularExpressions;
using CarsControl.Core.Util;

namespace CarsControl.Core.Winget;

/// <summary>Runs winget.exe for upgrades/uninstalls. Used unelevated (as you) for per-user apps and by
/// the elevated helper (as the admin account) for machine-wide ones. Listing uses the COM API instead.</summary>
public static partial class WingetCli
{
    /// <summary>winget ids are dotted publisher.package names; anything else is rejected before it can
    /// reach a command line.</summary>
    public static bool IsValidId(string id) => id.Length is > 0 and <= 128 && IdPattern().IsMatch(id);

    public static bool IsValidVersion(string v) => v.Length is > 0 and <= 64 && VersionPattern().IsMatch(v);

    /// <summary>Locates winget for the current account. The app execution alias lives in the user's
    /// WindowsApps folder; extra profiles (e.g. yours, when the helper runs as an admin account) are tried
    /// next, since that path is what worked for UniGetUI's elevated runs on this PC.</summary>
    public static string? Locate(params string?[] extraProfiles)
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe"),
        };
        candidates.AddRange(extraProfiles.Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.Combine(p!, @"AppData\Local\Microsoft\WindowsApps\winget.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static IReadOnlyList<string> UpgradeArgs(string id, string? version, string? logPath)
    {
        var args = new List<string>
        {
            "upgrade", "--id", id, "--exact", "--source", "winget",
            "--silent", "--disable-interactivity", "--include-unknown",
            "--accept-package-agreements", "--accept-source-agreements",
        };
        if (version is not null) args.AddRange(["--version", version]);
        if (logPath is not null) args.AddRange(["--log", logPath]);
        return args;
    }

    /// <summary>"user" installs for your account only (no admin); "machine" for every account (the helper).</summary>
    public static IReadOnlyList<string> InstallArgs(string id, string source, string scope, string? version, string? logPath)
    {
        var args = new List<string>
        {
            "install", "--id", id, "--exact", "--source", source, "--scope", scope,
            "--silent", "--disable-interactivity", "--accept-package-agreements", "--accept-source-agreements",
        };
        if (version is not null) args.AddRange(["--version", version]);
        if (logPath is not null) args.AddRange(["--log", logPath]);
        return args;
    }

    public static bool IsValidSource(string source) => source is "winget" or "msstore";

    public static IReadOnlyList<string> UninstallArgs(string id, string? logPath)
    {
        var args = new List<string>
        {
            "uninstall", "--id", id, "--exact", "--silent", "--disable-interactivity",
            "--accept-source-agreements",
        };
        if (logPath is not null) args.AddRange(["--log", logPath]);
        return args;
    }

    public static async Task<(WingetOutcome Outcome, ProcessResult Raw)> RunAsync(
        string wingetPath, IReadOnlyList<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var raw = await ProcessRunner.RunAsync(wingetPath, args, onLine, TimeSpan.FromMinutes(45), ct);
        return (WingetErrorMap.Interpret(raw.ExitCode, raw.Lines), raw);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]*$")]
    private static partial Regex VersionPattern();
}
