using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

// ToString overrides: DataGrid rows use them as their accessible names.
public sealed record FolderRow(DirNode Node, string Name, long Bytes, long Files, string SizeText, double Percent, string FilesText, bool IsFilesBlock)
{
    public override string ToString() => Name;
}

/// <summary>
/// Storage page. Split across files: this one has the drives, scanning and the Explore tab (with
/// back/forward history for the mouse side buttons); <c>StorageViewModel.Files.cs</c> is Big &amp; old
/// files and <c>StorageViewModel.Games.cs</c> is Games. Each list is filtered and sorted here (not by the
/// grid), so the Sort dropdown, the column-header arrows and the remembered choice always agree.
/// </summary>
public sealed partial class StorageViewModel : ObservableObject
{
    private readonly InventoryStore _inventory;
    private readonly HelperClient _helper;
    private readonly AppSettings _settings;
    private readonly Core.Data.CarsDb _db;
    private readonly Dictionary<string, ScanResult> _scans = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<DirNode> _back = new();
    private readonly Stack<DirNode> _forward = new();
    private CancellationTokenSource? _scanCts;

    /// <summary>True while saved choices are applied at startup: change handlers don't save or rebuild.</summary>
    private bool _loading;

    public ObservableCollection<DriveCard> Drives { get; } = [];
    public BulkObservableCollection<FolderRow> Folders { get; } = [];
    public ObservableCollection<DirNode> Breadcrumb { get; } = [];
    public ObservableCollection<OldWindowsPart> OldWindowsParts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScan), nameof(ShowEmptyState), nameof(ShowSummary))]
    private DirNode? _current;

    [ObservableProperty] private string? _scanDrive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowSummary))]
    private bool _isScanning;

    [ObservableProperty] private string? _scanStatus;
    [ObservableProperty] private double _scanPercent;
    [ObservableProperty] private bool _scanIndeterminate;
    [ObservableProperty] private string? _scanSummary;
    [ObservableProperty] private string? _scanNotice;
    [ObservableProperty] private string? _oldWindowsSummary;

    /// <summary>Tab title: "Old Windows" plus the drive it was found on (was hard-coded to D:).</summary>
    [ObservableProperty] private string _oldWindowsTab = "Old Windows";
    [ObservableProperty] private bool _isLoadingReports;

    /// <summary>Filters the folder list (not the treemap) by name.</summary>
    [ObservableProperty] private string _folderFilter = "";

    [ObservableProperty] private string _folderSortKey = "Bytes";
    [ObservableProperty] private bool _folderSortDescending = true;

    public bool HasScan => Current is not null;

    /// <summary>The summary line is hidden while a scan's progress line is showing.</summary>
    public bool ShowSummary => HasScan && !IsScanning;

    /// <summary>The "pick a drive" card shows only before any scan and while nothing is scanning
    /// (the hint used to sit underneath the progress text).</summary>
    public bool ShowEmptyState => Current is null && !IsScanning;

    public bool FastScanAvailable => WizTreeImport.IsInstalled && _settings.FastScan;

    public StorageViewModel(InventoryStore inventory, HelperClient helper, AppSettings settings, Core.Data.CarsDb db)
    {
        _inventory = inventory;
        _helper = helper;
        _settings = settings;
        _db = db;
        foreach (var r in ScanCache.LoadAll()) _scans[DriveOf(r.Root.FullPath)] = r;
        LoadSortChoices();
        if (_scans.Values.OrderByDescending(s => s.When).FirstOrDefault() is { } latest) Show(DriveOf(latest.Root.FullPath), latest, record: false);
        RefreshDrives();
        BuildFileDriveChips();
        BuildFileKindChips();
        // The file-drive chips follow the drives read off the UI thread; rebuild them when the set of drives changes.
        DriveSnapshot.Changed += () =>
        {
            if (!DriveSnapshot.Last.Select(d => d.Letter).SequenceEqual(FileDriveChips.Select(c => c.Key), StringComparer.OrdinalIgnoreCase)) OnScansChanged();
        };
    }

    private static string DriveOf(string path) => path[..2].ToUpperInvariant();

    /// <summary>Coalesces several property changes (a header click sets key and direction) into one
    /// rebuild after the current input has been handled.</summary>
    private static void Later(ref bool pending, Action rebuild)
    {
        if (pending) return;
        pending = true;
        Platform.OnUiThread(rebuild, background: true);
    }

    // ---------- drives and scanning ----------

    [RelayCommand]
    public void RefreshDrives() => BackgroundTasks.Run(RefreshDrivesAsync(), "storage drives");

    /// <summary>Reads the drives off the UI thread (a spun-down hard disk can take seconds), then labels them here.</summary>
    private async Task RefreshDrivesAsync()
    {
        var read = await DriveSnapshot.ReadAsync();
        DriveCard.Sync(Drives, read.Select(c => c with
        {
            IsActive = string.Equals(c.Letter, ScanDrive, StringComparison.OrdinalIgnoreCase),
            Note = _scans.TryGetValue(c.Letter, out var s) ? $"scanned {Format.Ago(s.When)}" : null,
        }).ToList());
    }

    /// <summary>Clicking a drive shows its saved scan instantly; Rescan (or a drive never scanned) scans it.</summary>
    [RelayCommand]
    private async Task ScanAsync(string? drive)
    {
        if (drive is null) return;
        if (_scans.TryGetValue(drive, out var cached))
        {
            Show(drive, cached);
            return;
        }
        await RunScanAsync(drive);
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (ScanDrive is null) return;
        await RunScanAsync(ScanDrive);
    }

    private async Task<bool> RunScanAsync(string drive)
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        IsScanning = true;
        ScanDrive = drive;
        ScanNotice = null;
        RefreshDrives();
        var sw = Stopwatch.StartNew();
        try
        {
            ScanResult? result = null;
            if (FastScanAvailable)
                result = await TryFastScanAsync(drive, ct);
            result ??= await FolderScanAsync(drive, ct);

            ScanCache.Save(result);
            _scans[drive] = result;
            ForgetHistory(drive); // those folder objects belong to the old scan
            PerfLog.Duration($"scan {drive} ({result.Method}, {result.Root.TotalFiles:N0} files)", sw.Elapsed);
            Show(drive, result);
            OnScansChanged();
            return true;
        }
        catch (OperationCanceledException)
        {
            ScanStatus = null;
            ScanNotice = "Scan cancelled.";
            return false;
        }
        finally
        {
            IsScanning = false;
            RefreshDrives();
        }
    }

    /// <summary>WizTree through the admin helper (one UAC prompt per session at most). Returns null to
    /// fall back to the folder scan when admin is declined or WizTree fails.</summary>
    private async Task<ScanResult?> TryFastScanAsync(string drive, CancellationToken ct)
    {
        ScanIndeterminate = true;
        ScanStatus = $"Fast scan of {drive} — reading the master file table…";
        try
        {
            var sw = Stopwatch.StartNew();
            var r = await _helper.SendAsync(new StorageScanOp(drive[..1]), null, false, ct);
            if (!r.Ok || r.Data is not { } csv) throw new InvalidOperationException(r.Message ?? "Fast scan failed.");
            var expected = Path.Combine(SecureDataDir.Root, "scans");
            if (!Path.GetFullPath(csv).StartsWith(expected + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected export location.");
            ScanStatus = $"Fast scan of {drive} — building the folder tree…";
            var mftTime = sw.Elapsed;
            return await Task.Run(() => WizTreeImport.Parse(csv, drive + "\\", mftTime, null, ct), ct);
        }
        catch (HelperCancelledException)
        {
            ScanNotice = "Admin approval was declined, so this used the slower folder scan (system folders stay hidden).";
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ScanNotice = $"Fast scan unavailable ({ex.Message}); used the folder scan instead.";
            return null;
        }
        finally
        {
            ScanIndeterminate = false;
        }
    }

    private async Task<ScanResult> FolderScanAsync(string drive, CancellationToken ct)
    {
        var info = new DriveInfo(drive);
        var used = info.TotalSize - info.TotalFreeSpace;
        var threads = DriveKind.ScanParallelism(drive + "\\");
        var kind = DriveKind.IsRotational(drive + "\\") == true ? "hard drive, 1 thread" : $"SSD, {threads} threads";
        var start = Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanPercent = used == 0 ? 0 : Math.Min(100, p.Bytes * 100.0 / used);
            var eta = ScanPercent > 3 ? TimeSpan.FromSeconds(start.Elapsed.TotalSeconds * (100 - ScanPercent) / ScanPercent) : (TimeSpan?)null;
            ScanStatus = $"Scanning {drive} ({kind}) — {ScanPercent:F0}% · {Format.Bytes(p.Bytes)} of {Format.Bytes(used)} · {p.Files:N0} files"
                         + (eta is { } e ? $" · about {(e.TotalMinutes >= 1 ? $"{e.TotalMinutes:F0} min" : $"{e.TotalSeconds:F0} s")} left" : "");
        });
        ScanStatus = $"Scanning {drive} ({kind})…";
        return await FolderScanner.ScanAsync(drive + "\\", progress, ct);
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private void Show(string drive, ScanResult r, bool record = true)
    {
        GoTo(r.Root, record);
        ScanStatus = null;
    }

    private void UpdateSummary(string drive)
    {
        if (!_scans.TryGetValue(drive, out var r)) return;
        var hidden = r.UnreadableBytes > 1_000_000_000 && r.Method != "Fast scan (MFT)"
            ? $" · {Format.Bytes(r.UnreadableBytes)} needs admin to see (system folders such as System Volume Information){(FastScanAvailable ? " — Rescan uses Fast scan" : "")}"
            : "";
        var cloud = r.OnlineOnlyBytes > 0 ? $" · plus {Format.Bytes(r.OnlineOnlyBytes)} of online-only cloud files that aren't on this disk" : "";
        ScanSummary = $"{drive} — {Format.Bytes(r.Root.TotalBytes)} on disk in {r.Root.TotalFiles:N0} files · {r.Method} in {r.Elapsed.TotalSeconds:F0}s · {(r.FromCache ? $"saved {Format.Ago(r.When)}" : "just now")}{cloud}{hidden}";
    }

    // ---------- Explore: navigation with history ----------

    partial void OnCurrentChanged(DirNode? value)
    {
        UpCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        Breadcrumb.Clear();
        if (value is not null)
        {
            for (var n = value; n is not null; n = n.Parent) Breadcrumb.Insert(0, n);
            var drive = DriveOf(value.FullPath);
            if (!string.Equals(drive, ScanDrive, StringComparison.OrdinalIgnoreCase))
            {
                ScanDrive = drive;
                RefreshDrives();
            }
            UpdateSummary(drive);
        }
        if (FolderFilter.Length > 0) FolderFilter = ""; // rebuilds the rows
        else RebuildFolders();
    }

    partial void OnFolderFilterChanged(string value) => RebuildFolders();
    partial void OnFolderSortKeyChanged(string value) => FolderSortChanged();
    partial void OnFolderSortDescendingChanged(bool value) => FolderSortChanged();

    private bool _foldersPending;

    private void FolderSortChanged()
    {
        if (_loading) return;
        _settings.SetUi("storage.folders.sort", $"{FolderSortKey}:{(FolderSortDescending ? "desc" : "asc")}");
        Later(ref _foldersPending, () =>
        {
            _foldersPending = false;
            RebuildFolders();
        });
    }

    private void RebuildFolders()
    {
        var value = Current;
        if (value is null)
        {
            Folders.ReplaceAll([]);
            return;
        }
        var rows = new List<FolderRow>();
        foreach (var c in value.Children)
            rows.Add(new FolderRow(c, c.Name + (c.Inaccessible ? "  (partly unreadable)" : ""), c.TotalBytes, c.TotalFiles, Format.Bytes(c.TotalBytes),
                value.TotalBytes == 0 ? 0 : c.TotalBytes * 100.0 / value.TotalBytes, $"{c.TotalFiles:N0} files", false));
        if (value.OwnFilesBytes > 0 && !value.Truncated)
            rows.Add(new FolderRow(value, "(files in this folder)", value.OwnFilesBytes, value.OwnFileCount, Format.Bytes(value.OwnFilesBytes),
                value.TotalBytes == 0 ? 0 : value.OwnFilesBytes * 100.0 / value.TotalBytes, $"{value.OwnFileCount:N0} files", true));

        IEnumerable<FolderRow> q = rows;
        if (FolderFilter.Trim() is { Length: > 0 } f) q = q.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
        q = FolderSortKey switch
        {
            "Name" => FolderSortDescending ? q.OrderByDescending(r => r.Name, StringComparer.CurrentCultureIgnoreCase) : q.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            "Files" => FolderSortDescending ? q.OrderByDescending(r => r.Files) : q.OrderBy(r => r.Files),
            _ => FolderSortDescending ? q.OrderByDescending(r => r.Bytes) : q.OrderBy(r => r.Bytes),
        };
        Folders.ReplaceAll(q);
    }

    /// <summary>Every navigation goes through here so Back/Forward (and the mouse side buttons) work like Explorer.</summary>
    private void GoTo(DirNode node, bool record = true)
    {
        if (record && Current is { } from && !ReferenceEquals(from, node))
        {
            _back.Push(from);
            _forward.Clear();
        }
        Current = null; // force the list/treemap to refresh even when re-entering the same (grafted) folder
        Current = node;
    }

    private bool CanGoBack() => _back.Count > 0;
    private bool CanGoForward() => _forward.Count > 0;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    public void Back()
    {
        if (_back.Count == 0) return;
        if (Current is { } c) _forward.Push(c);
        GoTo(_back.Pop(), record: false);
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    public void Forward()
    {
        if (_forward.Count == 0) return;
        if (Current is { } c) _back.Push(c);
        GoTo(_forward.Pop(), record: false);
    }

    private void ForgetHistory(string drive)
    {
        static Stack<DirNode> Without(Stack<DirNode> s, string d) =>
            new(s.Reverse().Where(n => !n.FullPath.StartsWith(d, StringComparison.OrdinalIgnoreCase)));
        var back = Without(_back, drive);
        var forward = Without(_forward, drive);
        _back.Clear();
        _forward.Clear();
        foreach (var n in back.Reverse()) _back.Push(n);
        foreach (var n in forward.Reverse()) _forward.Push(n);
    }

    /// <summary>Folders below the saved depth are rescanned on demand (seconds for one folder).</summary>
    [RelayCommand]
    private async Task NavigateAsync(DirNode? node)
    {
        if (node is null) return;
        if (node.Truncated && !IsScanning)
        {
            IsScanning = true;
            ScanIndeterminate = true;
            ScanStatus = $"Loading {node.Name}…";
            try
            {
                var sub = await FolderScanner.ScanAsync(node.FullPath, null, CancellationToken.None);
                node.Children.Clear();
                foreach (var c in sub.Root.Children) node.Children.Add(c.CloneUnder(node));
                node.OwnFilesBytes = sub.Root.OwnFilesBytes;
                node.OwnFileCount = sub.Root.OwnFileCount;
                node.Truncated = false;
            }
            finally
            {
                IsScanning = false;
                ScanIndeterminate = false;
                ScanStatus = null;
            }
        }
        if (node.Children.Count > 0 || node.Parent is null) GoTo(node);
    }

    /// <summary>The right-click "Folder size in C.A.R.S." entry: shows that folder in Explore, scanning its drive first
    /// when there's no saved scan. A folder newer than the saved scan is reached as far as it goes, with a note.</summary>
    public async Task ExplorePathAsync(string path)
    {
        if (path.Length < 2 || !char.IsAsciiLetter(path[0]) || path[1] != ':') return;
        var drive = DriveOf(path);
        if (!_scans.ContainsKey(drive) && !await RunScanAsync(drive)) return;
        if (!_scans.TryGetValue(drive, out var scan)) return;

        var node = scan.Root;
        var matched = true;
        var rest = path[Math.Min(path.Length, scan.Root.FullPath.Length)..].Trim('\\');
        foreach (var segment in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.Truncated) await NavigateAsync(node);
            var child = node.Children.FirstOrDefault(c => c.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (child is null) { matched = false; break; }
            node = child;
        }
        await NavigateAsync(node);
        ScanNotice = matched ? null : "That folder isn't in the saved scan (it may be newer than it). Rescan to see it.";
    }

    [RelayCommand]
    private Task OpenFolderRowAsync(FolderRow? row)
    {
        if (row is null) return Task.CompletedTask;
        if (row.IsFilesBlock || (row.Node.Children.Count == 0 && !row.Node.Truncated))
        {
            Explore(row.Node.FullPath);
            return Task.CompletedTask;
        }
        return NavigateAsync(row.Node);
    }

    private bool CanGoUp() => Current?.Parent is not null;

    /// <summary>Disabled at the drive root (it used to be clickable and silently do nothing there).</summary>
    [RelayCommand(CanExecute = nameof(CanGoUp))]
    public void Up()
    {
        if (Current?.Parent is { } p) GoTo(p);
    }

    [RelayCommand]
    private static void Explore(string? path)
    {
        if (path is null) return;
        Platform.ShowInFileManager(path);
    }

    [RelayCommand]
    private void ExploreCurrent() => Explore(Current?.FullPath);

    [RelayCommand]
    private static void CopyText(string? text)
    {
        Platform.CopyText(text);
    }

    // ---------- old Windows + reports shared with Home ----------

    public bool ReportsLoaded { get; private set; }

    [RelayCommand]
    public async Task LoadReportsAsync()
    {
        if (IsLoadingReports) return;
        IsLoadingReports = true;
        try
        {
            if (_inventory.Current is null) await _inventory.RefreshAsync();
            var apps = _inventory.Current?.Apps ?? [];
            var (games, libs) = await Task.Run(() => GameLibraries.Read(apps));
            SetGames(games, libs);
            BuildFileRows();

            var old = await Task.Run(() => OldWindowsReport.Find(apps));
            OldWindowsParts.Clear();
            if (old.FirstOrDefault() is { } w)
            {
                OldWindowsTab = $"Old Windows on {w.Drive.TrimEnd('\\')}";
                foreach (var p in w.Parts.OrderByDescending(p => p.Bytes)) OldWindowsParts.Add(p);
                OldWindowsSummary = $"A previous Windows installation is taking {Format.Bytes(w.TotalBytes)} on {w.Drive}"
                    + (w.Parts.Any(p => p.StillUsedBy.Count > 0) ? " — some of it is still used by installed apps (see below)." : " — no installed app points into it.")
                    + (w.KeepFolders.Count > 0 ? $" Keep: {string.Join(", ", w.KeepFolders)}." : "");
            }
            else
            {
                OldWindowsTab = "Old Windows";
                OldWindowsSummary = "No leftover Windows installations found on other drives.";
            }
            ReportsLoaded = true;
        }
        finally
        {
            IsLoadingReports = false;
        }
    }

    /// <summary>
    /// Guided removal of one part of a leftover Windows installation: shows what goes and what still points into it,
    /// asks once (twice for Users), then the admin helper takes ownership and deletes it. It's permanent: that's why the
    /// folder is listed and the personal files are pointed out first.
    /// </summary>
    [RelayCommand]
    private async Task RemoveOldWindowsPartAsync(OldWindowsPart? part)
    {
        if (part is null) return;
        if (part.StillUsedBy.Count > 0)
        {
            await Dialogs.InfoAsync($"{string.Join(", ", part.StillUsedBy.Take(6))} still points into {part.Path}, so it stays. Uninstall or move those first (Apps page), then come back.");
            return;
        }
        var isUsers = part.Path.EndsWith(@"\Users", StringComparison.OrdinalIgnoreCase);
        var warning = isUsers ? "\n\nThis holds the old user profiles: Documents, Desktop, Downloads, saved games and browser data of the old install. Copy anything you want to keep first (Open shows the folder)." : "";
        if (!await Dialogs.ConfirmAsync($"Remove {part.Path} ({Format.Bytes(part.Bytes)})?\n\n{part.Advice}{warning}\n\nIt's deleted for good, not sent to the Recycle Bin. C.A.R.S. takes ownership of the folder as admin (one approval), then deletes it. Your current Windows, game libraries and every other folder on that drive are not touched.", "Remove it", danger: true)) return;
        if (isUsers && !await Dialogs.ConfirmAsync("Last check: have you copied everything you want from the old Users folder? There is no undo.", "Yes, remove it", danger: true)) return;

        ScanNotice = $"Removing {part.Path}… this can take several minutes.";
        try
        {
            var r = await _helper.SendAsync(new RemoveOldWindowsOp(part.Path), line => Platform.OnUiThread(() => ScanNotice = line), HelperClient.DevUnelevated);
            _db.AddHistory(new Core.Data.HistoryEntry(0, DateTime.Now, DateTime.Now, "cleanup", "oldwindows:" + part.Path, part.Path, "Old Windows", null, null, true, r.Status.ToString(), r.ExitCode,
                r.Ok ? $"Removed {part.Path} ({Format.Bytes(part.Bytes)})" : r.Message, r.Message));
            ScanNotice = r.Ok ? $"Removed {part.Path}: {Format.Bytes(part.Bytes)} freed." : r.Message ?? "Windows didn't remove it.";
            if (r.Ok) await LoadReportsAsync();
        }
        catch (HelperCancelledException)
        {
            ScanNotice = "Admin approval was cancelled, so nothing was removed.";
        }
        catch (HelperUnavailableException ex)
        {
            ScanNotice = ex.Message;
        }
    }

    public string? LargestFolder(string drive) =>
        _scans.TryGetValue(drive, out var s) && s.Root.Children.FirstOrDefault() is { } top ? $"biggest: {top.Name} {Format.Bytes(top.TotalBytes)}" : null;

    private static (string Key, bool Descending)? ParseSort(string? saved)
    {
        if (saved?.Split(':') is not [var key, var dir] || key.Length == 0) return null;
        return (key, dir == "desc");
    }

    private void LoadSortChoices()
    {
        _loading = true;
        try
        {
            if (ParseSort(_settings.GetUi("storage.folders.sort")) is { } f)
            {
                FolderSortKey = f.Key;
                FolderSortDescending = f.Descending;
            }
            LoadFileChoices();
            LoadGameChoices();
        }
        finally
        {
            _loading = false;
        }
    }
}
