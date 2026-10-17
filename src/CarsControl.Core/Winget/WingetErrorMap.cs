using System.Text.RegularExpressions;

namespace CarsControl.Core.Winget;

public enum WingetFailure
{
    None,
    NotFoundForAccount,   // installed only for another account (elevated run can't see your per-user app)
    NotApplicable,        // "phantom" update: winget sees one, but no installer applies
    NoApplicableInstaller, // install: nothing fits this scope (e.g. no per-user installer)
    HashMismatch,         // winget's manifest is stale
    SourceBroken,
    AppInUse,
    InstallerFailed,
    InstallInProgress,
    DiskFull,
    NoNetwork,
    MsixFailed,
    Terminated,
    RebootRequired,
    Unknown,
}

public sealed record WingetOutcome(WingetFailure Failure, string Message, bool RebootRequired = false, int? InstallerExitCode = null)
{
    public bool Ok => Failure is WingetFailure.None or WingetFailure.RebootRequired;
}

/// <summary>Turns winget exit codes into plain English. The codes and wording come from the failures
/// seen in UniGetUI's history on this PC (2026-07-31 to 2026-10-11) plus winget's documented installer codes.</summary>
public static partial class WingetErrorMap
{
    public static WingetOutcome Interpret(int exitCode, IReadOnlyList<string> output)
    {
        var text = string.Join("\n", output);
        int? installerCode = InstallerExit().Match(text) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var ic) ? ic : null;

        var code = unchecked((uint)exitCode);
        // The app updated itself (or another tool did) after the check: winget exits 0x8A15002B with
        // "No newer package versions are available". That's up to date, not a failure.
        if (text.Contains("No newer package versions are available", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No available upgrade found", StringComparison.OrdinalIgnoreCase))
            return new(WingetFailure.None, "Already up to date");
        return code switch
        {
            0 => new(WingetFailure.None, "Updated"),
            0x8A150014 => new(WingetFailure.NotFoundForAccount, "Not installed for this account — it's a per-user app, so it has to update as you, not as admin."),
            0x8A150010 => new(WingetFailure.NoApplicableInstaller, "This app has no installer that fits that choice (many apps only install for every user of the PC)."),
            0x8A15002B => new(WingetFailure.NotApplicable, "winget lists an update but no installer applies (version mismatch). Skipping this version is usually right."),
            0x8A150011 => new(WingetFailure.HashMismatch, "The download doesn't match winget's manifest — winget's catalog is out of date for this app. Try again in a day or two."),
            0x8A15000F => new(WingetFailure.SourceBroken, "winget's source data is missing for the admin account. Sign in to the admin account once and run `winget source update`."),
            0x8A150101 or 0x8A150103 => new(WingetFailure.AppInUse, "The app (or one of its files) is in use. Close it and try again."),
            0x8A150102 => new(WingetFailure.InstallInProgress, "Another installation is running. Try again when it finishes."),
            0x8A150105 => new(WingetFailure.DiskFull, "Not enough disk space."),
            0x8A150107 => new(WingetFailure.NoNetwork, "No network connection."),
            0x8A150109 or 0x8A15010A => new(WingetFailure.RebootRequired, "Updated — restart needed to finish.", RebootRequired: true),
            // MSIX packages register for the installing account. Elevated, that's the admin account, which isn't
            // signed in, so Windows refuses (WSL via UniGetUI, 2026-08-19 and 09-05). Same remedy as
            // 0x8A150014: run it as you.
            0x80073D19 => new(WingetFailure.NotFoundForAccount, "This package installs per-user and the admin account isn't signed in (0x80073D19) — it has to update as you."),
            0x80073CF9 => new(WingetFailure.MsixFailed, "The package install failed. This app should be updated through its own channel instead of winget."),
            0x40010004 => new(WingetFailure.Terminated, "The installer was terminated part-way (usually the app was running or closed mid-update)."),
            _ when installerCode is not null => InstallerFailure(installerCode.Value),
            _ when text.Contains("No installed package found", StringComparison.OrdinalIgnoreCase) => Interpret(unchecked((int)0x8A150014), []),
            _ when text.Contains("No applicable installer found", StringComparison.OrdinalIgnoreCase) => Interpret(unchecked((int)0x8A150010), []),
            _ when text.Contains("No applicable upgrade found", StringComparison.OrdinalIgnoreCase) => Interpret(unchecked((int)0x8A15002B), []),
            _ => new(WingetFailure.Unknown, $"winget exited with 0x{code:X8}."),
        };
    }

    private static WingetOutcome InstallerFailure(int ic) => ic switch
    {
        3010 or 1641 => new(WingetFailure.RebootRequired, "Updated — restart needed to finish.", true, ic),
        1618 => new(WingetFailure.InstallInProgress, "Another installation is running (MSI 1618). Try again when it finishes.", InstallerExitCode: ic),
        1603 => new(WingetFailure.InstallerFailed, "The installer hit a fatal error (MSI 1603). Common causes: the app is running, or a restart is pending.", InstallerExitCode: ic),
        1602 or 1223 => new(WingetFailure.InstallerFailed, "The installer was cancelled.", InstallerExitCode: ic),
        _ => new(WingetFailure.InstallerFailed, $"The installer failed with exit code {ic}.", InstallerExitCode: ic),
    };

    [GeneratedRegex(@"Installer failed with exit code:\s*(-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerExit();
}
