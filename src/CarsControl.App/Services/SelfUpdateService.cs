using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using CarsControl.Core.Settings;
using CarsControl.Core.Util;
using CarsControl.Setup;

namespace CarsControl.App.Services;

/// <summary>
/// C.A.R.S. Control updating itself from its GitHub Releases. Checks at start and daily (never during a game),
/// downloads CarsControl-Setup-x.y.z.exe, verifies it against the release's SHA256SUMS.txt, shows what's
/// new, and on "Update now" hands over to Setup (<c>--update</c>): Setup closes the app, replaces the files
/// (rolling back on failure), and starts it again. One admin prompt.
/// <para>Public repository: plain HTTPS, no sign-in. Private repository (now): through the GitHub CLI you're
/// already signed into, so no token is stored anywhere. Nothing is installed without your click.</para>
/// </summary>
public sealed class SelfUpdateService
{
    public void Stop() => _timer.Stop();
    private static readonly TimeSpan Every = TimeSpan.FromHours(24);

    private readonly AppSettings _settings;
    private readonly GameMode _game;
    private readonly AttentionService _attention;
    private readonly DispatcherTimer _timer = new();
    private string? _downloadedPath;

    public SelfUpdateService(AppSettings settings, GameMode game, AttentionService attention)
    {
        _settings = settings;
        _game = game;
        _attention = attention;
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Every;
            if (UpdateSettings.AutoCheck && !_game.IsOn && !IsBusy) await CheckAsync(manual: false);
        };
    }

    private UpdateSettings UpdateSettings => _settings.Store.Current.Updates;

    /// <summary>What's running, from the informational version the release build stamps ("0.8.0-rc.1").</summary>
    public static SemVer Current
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var text = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "0.0.0";
            return SemVer.TryParse(text, out var v) ? v : new SemVer(0, 0, 0);
        }
    }

    /// <summary>Only the installed copy updates itself; a build run from a development folder or a test copy
    /// has no business replacing Program Files (CARS_DEV_UPDATE=1 allows it for testing).</summary>
    public static bool IsInstalledCopy =>
        AppContext.BaseDirectory.StartsWith(Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
        || Environment.GetEnvironmentVariable("CARS_DEV_UPDATE") == "1";

    public ReleaseInfo? Available { get; private set; }
    public bool IsBusy { get; private set; }
    public bool Ready => Available is not null && _downloadedPath is not null && File.Exists(_downloadedPath);
    public string Status { get; private set; } = "Not checked yet";

    /// <summary>Raised on the UI thread.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (!IsInstalledCopy || App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEV_UPDATE") != "1") return;
        _timer.Interval = TimeSpan.FromSeconds(75); // after startup has settled
        _timer.Start();
    }

    public async Task CheckAsync(bool manual)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Checking GitHub for a newer version…";
        Raise();
        try
        {
            var (json, via) = await FetchReleasesAsync();
            _settings.SetUi("selfupdate.last", DateTime.Now.ToString("o"));
            if (json is null)
            {
                Status = "Couldn't reach GitHub" + (manual ? " (are you online? For a private repository, is the GitHub CLI signed in?)" : "");
                return;
            }
            var pick = ReleaseInfo.PickUpdate(json, Current, UpdateSettings.IncludePreReleases);
            if (pick is null || (!manual && pick.Version.ToString() == UpdateSettings.SkippedVersion))
            {
                Available = null;
                _downloadedPath = null;
                Status = $"You have the latest version ({Current}). Checked {DateTime.Now:HH:mm}.";
                _attention.Clear("selfupdate");
                return;
            }

            Available = pick;
            Status = $"Version {pick.Version} is available. Downloading it…";
            Raise();
            var error = await DownloadAsync(pick, via);
            if (error is not null)
            {
                _downloadedPath = null;
                Status = $"Version {pick.Version} is available, but the download didn't check out: {error}";
                return;
            }
            Status = $"Version {pick.Version} is ready to install.";
            PostAttention(pick);
            if (!manual) Notifier.AppUpdateReady(pick.Version.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or System.Text.Json.JsonException)
        {
            ErrorReporter.Log("self-update check", ex);
            Status = "The update check failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            Raise();
        }
    }

    private void PostAttention(ReleaseInfo r) => _attention.Set("selfupdate", new AttentionItem
    {
        Key = $"selfupdate:{r.Version}",
        Title = $"C.A.R.S. Control {r.Version} is ready to install",
        Detail = "A new version is downloaded and verified. Your settings and data are kept; one admin prompt.",
        Icon = Wpf.Ui.Controls.SymbolRegular.ArrowCircleUp24,
        Level = AttentionLevel.Info,
        ActionText = "Update now",
        Action = ApplyAsync,
        SnoozeFor = TimeSpan.FromDays(1),
    });

    /// <summary>Hands over to Setup. The verified file is held open while it starts, so nothing can swap it
    /// between the check and the launch.</summary>
    public async Task ApplyAsync()
    {
        if (!Ready || _downloadedPath is not { } path) return;
        // Re-check right before launching: the file sits in a folder you can write to.
        if (await VerifyAsync(path, Available!) is { } problem)
        {
            Status = "The downloaded installer failed its check again, so it wasn't run: " + problem;
            _downloadedPath = null;
            Raise();
            return;
        }
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Its own folder as working directory: a Setup "standing in" the install folder can't swap it.
            Process.Start(new ProcessStartInfo(path, "--update") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! })?.Dispose();
        Status = "Setup is taking over…";
        Raise();
        await Task.Delay(4000); // keep the file pinned while Setup starts; it asks this app to close itself
    }

    public void Skip()
    {
        if (Available is null) return;
        _settings.Store.Update(s => s.Updates.SkippedVersion = Available.Version.ToString());
        _attention.Clear("selfupdate");
        Available = null;
        _downloadedPath = null;
        Status = "Skipped. You'll hear about the next version.";
        Raise();
    }

    // ---------- GitHub ----------

    private enum Via { Http, Gh }

    private async Task<(string? Json, Via Via)> FetchReleasesAsync()
    {
        var repo = UpdateSettings.Repository;
        using (var http = NewHttp())
        {
            try
            {
                using var response = await http.GetAsync($"https://api.github.com/repos/{repo}/releases?per_page=30");
                if (response.IsSuccessStatusCode) return (await response.Content.ReadAsStringAsync(), Via.Http);
            }
            catch (HttpRequestException)
            {
                // Offline, or blocked: the CLI may still know a way.
            }
        }
        // A private repository answers "not found" to anyone who isn't signed in: ask through the GitHub CLI.
        if (FindGh() is { } gh)
        {
            var r = await ProcessRunner.RunAsync(gh, ["api", $"repos/{repo}/releases?per_page=30"], null, TimeSpan.FromSeconds(30));
            if (r.ExitCode == 0 && !r.TimedOut && r.Stdout.TrimStart().StartsWith('[')) return (r.Stdout, Via.Gh);
        }
        return (null, Via.Http);
    }

    private async Task<string?> DownloadAsync(ReleaseInfo release, Via via)
    {
        var setup = release.SetupAsset!;
        var dir = Path.Combine(Core.Util.UserData.Root, "updates", release.Version.ToString());
        Directory.CreateDirectory(dir);
        PruneOldDownloads(dir);
        var file = Path.Combine(dir, setup.Name);
        var sums = Path.Combine(dir, ReleaseInfo.ChecksumsAsset);

        if (via == Via.Http)
        {
            using var http = NewHttp();
            await Download(http, release.Checksums!.DownloadUrl, sums);
            if (!File.Exists(file) || new FileInfo(file).Length != setup.Size) await Download(http, setup.DownloadUrl, file);
        }
        else
        {
            var gh = FindGh() ?? throw new IOException("The GitHub CLI went missing.");
            var r = await ProcessRunner.RunAsync(gh,
                ["release", "download", release.Tag, "--repo", UpdateSettings.Repository, "--pattern", setup.Name, "--pattern", ReleaseInfo.ChecksumsAsset, "--dir", dir, "--clobber"],
                null, TimeSpan.FromMinutes(10));
            if (r.ExitCode != 0) return "the GitHub CLI couldn't download it (" + r.Tail(2) + ")";
        }

        _downloadedPath = file;
        return await VerifyAsync(file, release);
    }

    private static async Task Download(HttpClient http, string url, string path)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var src = await response.Content.ReadAsStreamAsync();
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst);
    }

    /// <summary>Null when the file's SHA-256 is the one in the release's SHA256SUMS.txt.</summary>
    private static async Task<string?> VerifyAsync(string file, ReleaseInfo release)
    {
        var sumsPath = Path.Combine(Path.GetDirectoryName(file)!, ReleaseInfo.ChecksumsAsset);
        if (!File.Exists(sumsPath) || !File.Exists(file)) return "the checksum list or the file is missing";
        var expected = Checksums.HashFor(await File.ReadAllTextAsync(sumsPath), release.SetupAsset!.Name);
        if (expected is null) return "the release's checksum list doesn't mention the installer";
        string actual;
        await using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return "its checksum doesn't match the release's, so it was deleted";
        }
        return null;
    }

    /// <summary>Only the version being offered is kept.</summary>
    private static void PruneOldDownloads(string keep)
    {
        // A leftover folder is harmless: one that's in use (an old Setup still running) or locked is skipped, never fatal.
        string[] others;
        try { others = Directory.GetDirectories(Path.GetDirectoryName(keep)!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (var other in others.Where(d => !string.Equals(d, keep, StringComparison.OrdinalIgnoreCase)))
        {
            try { Directory.Delete(other, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* try again next time */ }
        }
    }

    private static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CarsControl/" + Current);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static string? FindGh()
    {
        var candidates = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe") };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => Path.Combine(p.Trim(), "gh.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    private void Raise() => Platform.OnUiThread(() => Changed?.Invoke());
}
