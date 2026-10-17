using System.Diagnostics;
using System.Text.Json;
using CarsControl.Core.Inventory;
using CarsControl.Core.Models;
using CarsControl.Core.Settings;
using CarsControl.Core.Util;
using Microsoft.Win32;

namespace CarsControl.Core.Updates;

/// <summary>Every update check runs unelevated as you. Only installing machine-wide things needs the helper.</summary>
public static class UpdateSources
{
    public static async Task<SourceCheck> Timed(UpdateSource source, Func<Task<IReadOnlyList<UpdateCandidate>>> check)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return new SourceCheck(source, await check(), null, sw.Elapsed);
        }
        catch (Exception ex)
        {
            return new SourceCheck(source, [], ex.Message, sw.Elapsed);
        }
    }

    /// <summary>winget updates come straight from the inventory scan (COM API, correlated to ARP/MSIX).</summary>
    public static IReadOnlyList<UpdateCandidate> Winget(InventorySnapshot snapshot) =>
        snapshot.Apps
            .Where(a => a.HasUpdate && a.WingetId is not null && a.Channel == UpdateChannel.Winget)
            .Select(a => new UpdateCandidate
            {
                Key = $"winget:{a.WingetId}",
                Source = UpdateSource.Winget,
                Title = a.Name,
                CurrentVersion = a.Version,
                AvailableVersion = a.AvailableVersion,
                NeedsAdmin = a.Scope == InstallScope.Machine,
                App = a,
                PackageId = a.WingetId,
            })
            .ToList();

    /// <summary>WSL updates through `wsl --update`, run as you. It is an MSIX package that registers for
    /// the installing account; elevated (as an admin account that isn't signed in) winget failed twice here with
    /// 0x80073D19 "a user was logged off". WSL raises its own UAC prompt if its service needs one.</summary>
    public static IReadOnlyList<UpdateCandidate> Wsl(InventorySnapshot snapshot) =>
        snapshot.Apps
            .Where(a => a.Channel == UpdateChannel.Wsl && a.HasUpdate)
            .Take(1)
            .Select(a => new UpdateCandidate
            {
                Key = "wsl",
                Source = UpdateSource.Wsl,
                Title = "Windows Subsystem for Linux",
                CurrentVersion = a.Version,
                AvailableVersion = a.AvailableVersion,
                NeedsAdmin = false,
                App = a,
                Note = "Updated with `wsl --update` as you — WSL may ask for admin approval itself",
            })
            .ToList();

    public static async Task<IReadOnlyList<UpdateCandidate>> WindowsUpdateAsync(CancellationToken ct)
    {
        var updates = await WindowsUpdateApi.SearchAsync(ct);
        return updates.Where(u => !CoveredByOtherSource(u.Title)).Select(u => new UpdateCandidate
        {
            Key = $"wu:{u.UpdateId}",
            Source = UpdateSource.WindowsUpdate,
            Title = u.Title,
            AvailableVersion = u.KbArticle,
            NeedsAdmin = true,
            IsDriver = u.IsDriver,
            WuUpdateId = u.UpdateId,
            DownloadBytes = u.MaxDownloadBytes > 0 ? u.MaxDownloadBytes : null,
            // Settings › This PC can hold driver updates back (on the development PC: the dxgkrnl BSOD investigation).
            SelectedByDefault = !(u.IsDriver && ActiveProfile.Current.HoldDriverUpdates),
            Note = !u.IsDriver ? null
                : ActiveProfile.Current.HoldDriverUpdates ? $"Driver — unticked by default: {ActiveProfile.Current.HoldDriverUpdatesReason ?? "you're holding driver updates"}"
                : "Driver update",
        }).ToList();
    }

    /// <summary>WSL is also offered through Windows Update, but the WSL source updates it (as you, which
    /// is the only way that works here). Listing both ran it twice and the second "failed".</summary>
    internal static bool CoveredByOtherSource(string title) =>
        title.StartsWith("Windows Subsystem for Linux", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Store can't list per-app updates without admin, so it is one row that triggers its scan.</summary>
    public static IReadOnlyList<UpdateCandidate> Store() =>
    [
        new UpdateCandidate
        {
            Key = "store",
            Source = UpdateSource.Store,
            Title = "Microsoft Store apps",
            NeedsAdmin = true,
            Note = "Asks the Store to check and install updates for all Store apps",
        },
    ];

    /// <summary>Microsoft 365 (Click-to-Run). OfficeC2RClient /update user works unelevated (verified 2026-08-17).</summary>
    public static IReadOnlyList<UpdateCandidate> Office()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
        if (k is null || !File.Exists(Tools.OfficeC2RClient)) return [];
        var version = k.GetValue("VersionToReport") as string;
        return
        [
            new UpdateCandidate
            {
                Key = "office",
                Source = UpdateSource.Office,
                Title = "Microsoft 365 Apps",
                CurrentVersion = version,
                NeedsAdmin = false,
                Note = "Office checks and installs its own update; open Office apps finish updating when closed",
            },
        ];
    }

    public static async Task<IReadOnlyList<UpdateCandidate>> NpmAsync(CancellationToken ct)
    {
        var npm = Tools.Npm();
        if (npm is null) return [];
        // No global prefix folder = no global packages (npm itself errors with ENOENT in that case).
        if (!Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"))) return [];
        // `npm outdated` exits 1 when something is outdated; the JSON is still on stdout.
        var r = await ProcessRunner.RunAsync(Tools.Cmd, ["/d", "/c", npm, "outdated", "-g", "--json"], timeout: TimeSpan.FromMinutes(2), ct: ct);
        var json = r.Stdout.Trim();
        if (json.Length == 0) return [];
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error", out var err))
            throw new InvalidOperationException(err.TryGetProperty("summary", out var s) ? s.GetString() : "npm reported an error");
        var list = new List<UpdateCandidate>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (!Tools.IsSafePackageName(p.Name)) continue;
            var current = p.Value.TryGetProperty("current", out var c) ? c.GetString() : null;
            var latest = p.Value.TryGetProperty("latest", out var l) ? l.GetString() : null;
            if (latest is null || latest == current) continue;
            list.Add(new UpdateCandidate
            {
                Key = $"npm:{p.Name}",
                Source = UpdateSource.Npm,
                Title = p.Name,
                CurrentVersion = current,
                AvailableVersion = latest,
                PackageId = p.Name,
                NeedsAdmin = false, // global prefix is %APPDATA%\npm
            });
        }
        return list;
    }

    public static async Task<IReadOnlyList<UpdateCandidate>> PipAsync(CancellationToken ct)
    {
        var python = Tools.MachinePython();
        if (python is null) return [];
        // -v adds each package's "location", which decides who upgrades it (see PipInstall).
        var r = await ProcessRunner.RunAsync(python, ["-m", "pip", "list", "--outdated", "--format=json", "-v", "--disable-pip-version-check"], timeout: TimeSpan.FromMinutes(3), ct: ct);
        return ParsePipOutdated(r.Stdout);
    }

    internal static IReadOnlyList<UpdateCandidate> ParsePipOutdated(string stdout, string? userProfile = null)
    {
        var json = stdout.Trim();
        if (!json.StartsWith('[')) return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => (name: e.GetProperty("name").GetString()!, cur: e.GetProperty("version").GetString(), latest: e.GetProperty("latest_version").GetString(),
                location: e.TryGetProperty("location", out var l) ? l.GetString() : null))
            .Where(x => Tools.IsSafePackageName(x.name))
            .Select(x =>
            {
                var mine = PipInstall.IsUserLocation(x.location, userProfile);
                return new UpdateCandidate
                {
                    Key = $"pip:{x.name}",
                    Source = UpdateSource.Pip,
                    Title = x.name,
                    CurrentVersion = x.cur,
                    AvailableVersion = x.latest,
                    PackageId = x.name,
                    Location = x.location,
                    NeedsAdmin = !mine, // shared copies live in Program Files; yours are upgraded as you
                    SelectedByDefault = false,
                    Note = mine
                        ? "Installed just for you — upgrades as you, no admin. Upgrades can break pinned dependencies, so it's unticked by default"
                        : "Shared Python install — needs admin. Upgrades can break pinned dependencies, so it's unticked by default",
                };
            })
            .ToList();
    }

    public static async Task<IReadOnlyList<UpdateCandidate>> PsModulesAsync(CancellationToken ct)
    {
        var pwsh = Tools.Pwsh();
        if (pwsh is null) return [];
        // Versions are compared here, not in PowerShell: [version]'1.0.6.0' -gt [version]'1.0.6' is
        // true there, which reported Microsoft.WinGet.CommandNotFound as outdated when it wasn't.
        const string script =
            "$ErrorActionPreference='SilentlyContinue'; " +
            "$out = foreach ($m in (Get-InstalledPSResource -Scope CurrentUser | Group-Object Name)) { " +
            "  $cur = ($m.Group | Sort-Object Version -Descending | Select-Object -First 1); " +
            "  $latest = Find-PSResource -Name $cur.Name -Repository PSGallery; " +
            "  if ($latest) { [pscustomobject]@{ name=$cur.Name; current=\"$($cur.Version)\"; latest=\"$($latest.Version)\" } } }; " +
            "ConvertTo-Json -InputObject @($out) -Compress";
        var r = await ProcessRunner.RunAsync(pwsh, ["-NoProfile", "-NonInteractive", "-Command", script], timeout: TimeSpan.FromMinutes(3), ct: ct);
        var json = r.Stdout.Trim();
        if (!json.StartsWith('[')) return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => (name: e.GetProperty("name").GetString()!, cur: e.GetProperty("current").GetString(), latest: e.GetProperty("latest").GetString()))
            .Where(x => Tools.IsSafePackageName(x.name) && VersionCompare.IsNewer(x.latest, x.cur))
            .Select(x => new UpdateCandidate
            {
                Key = $"psmodule:{x.name}",
                Source = UpdateSource.PsModule,
                Title = x.name,
                CurrentVersion = x.cur,
                AvailableVersion = x.latest,
                PackageId = x.name,
                NeedsAdmin = false, // installed for you (Documents\PowerShell\Modules)
            })
            .ToList();
    }
}
