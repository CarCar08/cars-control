using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed record BigFileRow(BigFile File, string Name, string Folder, string Drive, FileKind Kind, Ownership Owner, DateTime? Touched)
{
    public long Bytes => File.Bytes;
    public string SizeText => Format.Bytes(File.Bytes);

    /// <summary>Days since last change; unknown dates sort as the oldest.</summary>
    public double AgeDays => Touched is { } t ? (DateTime.Now - t).TotalDays : double.MaxValue;

    public string AgeText => Touched is { } t ? Format.Age(t) : "unknown";
    public string AgeTip => Touched is { } t ? $"Last changed {t:yyyy-MM-dd}" : "No real date: the file is stamped 1980 or earlier (common after an interrupted copy)";
    public string KindText => FileKinds.Label(Kind);
    public bool CanRecycle => Owner.CanRecycle;
    public bool CanFreeUp => Owner.CanFreeUp;
    public bool IsWarning => Owner.IsWarning;
    public string OwnerText => Owner.Kind == OwnerKind.Free ? "" : Owner.Text;

    /// <summary>The row's main button: cloud files are freed up (kept online), the rest recycled.</summary>
    public string ActionText => CanFreeUp ? "Free up" : "Recycle";

    public bool CanAct => CanFreeUp || CanRecycle;
    public string ActionTip => CanFreeUp ? "Keep it in the cloud, remove the copy on this disk" : CanRecycle ? "Send to the Recycle Bin" : Owner.Text;
    public override string ToString() => Name;
}

/// <summary>Storage › Big &amp; old files: the biggest files from every scanned drive you pick, with
/// type chips, age/owner/size filters, search, and one sort shared by the dropdown and the headers.</summary>
public sealed partial class StorageViewModel
{
    private List<BigFileRow> _fileRows = [];

    /// <summary>Big files from the saved scans, unfiltered (the command palette searches them).</summary>
    public IReadOnlyList<BigFileRow> AllFiles => _fileRows;
    private readonly HashSet<string> _goneFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _filesPending;
    private bool _syncingFileSort;
    private IReadOnlyList<BigFileRow> _selectedFiles = [];

    public BulkObservableCollection<BigFileRow> Files { get; } = [];
    public ObservableCollection<FilterChip> FileDriveChips { get; } = [];
    public ObservableCollection<FilterChip> FileKindChips { get; } = [];

    public IReadOnlyList<Choice<int>> FileAgeOptions { get; } =
    [
        new(0, "Any age"), new(30, "Untouched 1+ month"), new(90, "Untouched 3+ months"), new(180, "Untouched 6+ months"),
        new(365, "Untouched 1+ year"), new(1095, "Untouched 3+ years"),
    ];

    public IReadOnlyList<Choice<string>> FileOwnerOptions { get; } =
    [
        new("any", "Any owner"), new("safe", "Safe to recycle"), new("cloud", "Cloud files (can free up)"),
        new("appdata", "App data (careful)"), new("apps", "Part of an app or game"), new("system", "Windows & system"),
    ];

    public IReadOnlyList<Choice<long>> FileSizeOptions { get; } =
    [
        new(0, "Any size"), new(100L << 20, "100 MB+"), new(500L << 20, "500 MB+"), new(1L << 30, "1 GB+"), new(10L << 30, "10 GB+"),
    ];

    public IReadOnlyList<SortOption> FileSortOptions { get; } =
    [
        new("Bytes", "Size", true), new("AgeDays", "Untouched longest", true), new("Name", "Name", false),
        new("Folder", "Folder", false), new("Drive", "Drive", false), new("OwnerText", "Belongs to", false), new("KindText", "Type", false),
    ];

    [ObservableProperty] private string _fileSearch = "";
    [ObservableProperty] private Choice<int> _fileAge = null!;
    [ObservableProperty] private Choice<string> _fileOwner = null!;
    [ObservableProperty] private Choice<long> _fileMinSize = null!;
    [ObservableProperty] private string _fileSortKey = "Bytes";
    [ObservableProperty] private bool _fileSortDescending = true;
    [ObservableProperty] private SortOption? _selectedFileSort;
    [ObservableProperty] private string? _filesSummary;
    [ObservableProperty] private string? _filesNotice;
    [ObservableProperty] private string? _fileSelectionSummary;
    [ObservableProperty] private bool _hasFileFilters;

    /// <summary>The type chips and filter dropdowns fold away (small windows); remembered.</summary>
    [ObservableProperty] private bool _showFileFilters = true;

    [ObservableProperty] private string _filtersButtonText = "Filters";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileSelection))]
    [NotifyCanExecuteChangedFor(nameof(RecycleSelectedCommand), nameof(FreeUpSelectedCommand), nameof(ShowSelectedCommand), nameof(CopySelectedPathCommand))]
    private int _selectedFileCount;

    public bool HasFileSelection => SelectedFileCount > 0;

    private FileKind? _kindFilter;

    private void LoadFileChoices()
    {
        ShowFileFilters = _settings.GetUi("storage.files.filters") != "hidden";
        FileAge = FileAgeOptions[0];
        FileOwner = FileOwnerOptions[0];
        FileMinSize = FileSizeOptions[0];
        if (ParseSort(_settings.GetUi("storage.files.sort")) is { } s && FileSortOptions.Any(o => o.Key == s.Key))
        {
            FileSortKey = s.Key;
            FileSortDescending = s.Descending;
        }
        SelectedFileSort = FileSortOptions.First(o => o.Key == FileSortKey);
    }

    partial void OnFileSearchChanged(string value) => FilesChanged();

    partial void OnShowFileFiltersChanged(bool value)
    {
        if (!_loading) _settings.SetUi("storage.files.filters", value ? null : "hidden");
    }
    partial void OnFileAgeChanged(Choice<int> value) => FilesChanged();
    partial void OnFileOwnerChanged(Choice<string> value) => FilesChanged();
    partial void OnFileMinSizeChanged(Choice<long> value) => FilesChanged();
    partial void OnFileSortKeyChanged(string value) => FileSortChanged();
    partial void OnFileSortDescendingChanged(bool value) => FileSortChanged();

    partial void OnSelectedFileSortChanged(SortOption? value)
    {
        if (value is null || _syncingFileSort || _loading || value.Key == FileSortKey) return;
        _syncingFileSort = true;
        FileSortKey = value.Key;
        FileSortDescending = value.DescendingFirst;
        _syncingFileSort = false;
        FileSortChanged();
    }

    private void FileSortChanged()
    {
        if (_syncingFileSort || _loading) return;
        _syncingFileSort = true;
        SelectedFileSort = FileSortOptions.FirstOrDefault(o => o.Key == FileSortKey);
        _syncingFileSort = false;
        _settings.SetUi("storage.files.sort", $"{FileSortKey}:{(FileSortDescending ? "desc" : "asc")}");
        FilesChanged();
    }

    [RelayCommand]
    private void ToggleFileSortDirection() => FileSortDescending = !FileSortDescending;

    private void FilesChanged()
    {
        if (_loading) return;
        Later(ref _filesPending, () =>
        {
            _filesPending = false;
            ApplyFileFilters();
        });
    }

    // ---------- drive chips ----------

    /// <summary>One chip per fixed drive. Scanned drives are on unless you turned them off last time;
    /// clicking a drive that was never scanned scans it.</summary>
    private void BuildFileDriveChips()
    {
        var saved = _settings.GetUi("storage.files.off")?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var previous = FileDriveChips.ToDictionary(c => c.Key, c => c.IsSelected, StringComparer.OrdinalIgnoreCase);
        foreach (var c in FileDriveChips) c.PropertyChanged -= OnFileDriveChipChanged;
        FileDriveChips.Clear();
        foreach (var d in DriveCard.ReadAll())
        {
            var scanned = _scans.TryGetValue(d.Letter, out var s);
            var chip = new FilterChip(d.Letter, d.Letter)
            {
                Detail = scanned ? Format.Bytes(s!.Root.TotalBytes) : "not scanned",
                ToolTip = scanned ? $"{d.Label} · scanned {Format.Ago(s!.When)} with {s.Method}" : $"{d.Label} · click to scan it",
                IsAvailable = scanned,
                IsSelected = scanned && (previous.TryGetValue(d.Letter, out var was) ? was : !saved.Contains(d.Letter)),
            };
            chip.PropertyChanged += OnFileDriveChipChanged;
            FileDriveChips.Add(chip);
        }
    }

    private async void OnFileDriveChipChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FilterChip.IsSelected) || sender is not FilterChip chip) return;
        _settings.SetUi("storage.files.off", string.Join(',', FileDriveChips.Where(c => c.IsAvailable && !c.IsSelected).Select(c => c.Key)));
        if (chip.IsSelected && !_scans.ContainsKey(chip.Key))
        {
            try
            {
                if (IsScanning || !await RunScanAsync(chip.Key)) chip.IsSelected = false;
            }
            catch (Exception ex)
            {
                chip.IsSelected = false;
                FilesNotice = $"Couldn't scan {chip.Key}: {ex.Message}";
            }
            return; // a finished scan rebuilds everything
        }
        BuildFileRows();
    }

    [RelayCommand]
    private void SelectAllFileDrives()
    {
        foreach (var c in FileDriveChips.Where(c => c.IsAvailable)) c.IsSelected = true;
    }

    private void OnScansChanged()
    {
        BuildFileDriveChips();
        BuildFileRows();
    }

    // ---------- type chips ----------

    private void BuildFileKindChips()
    {
        var all = new FilterChip("all", "All types") { IsSelected = true };
        all.PropertyChanged += OnFileKindChipChanged;
        FileKindChips.Add(all);
        foreach (var k in FileKinds.All)
        {
            var chip = new FilterChip(k.ToString(), FileKinds.Label(k));
            chip.PropertyChanged += OnFileKindChipChanged;
            FileKindChips.Add(chip);
        }
    }

    private bool _syncingKinds;

    /// <summary>Type chips pick one type at a time (clicking the selected one goes back to all).</summary>
    private void OnFileKindChipChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FilterChip.IsSelected) || sender is not FilterChip chip || _syncingKinds) return;
        _syncingKinds = true;
        var pick = chip.IsSelected ? chip.Key : "all";
        foreach (var c in FileKindChips) c.IsSelected = c.Key == pick;
        _kindFilter = Enum.TryParse<FileKind>(pick, out var k) ? k : null;
        _syncingKinds = false;
        FilesChanged();
    }

    // ---------- rows ----------

    /// <summary>Classifies the files of every selected drive once; filters and sorting reuse the rows.</summary>
    private void BuildFileRows()
    {
        var owners = new FileOwnership(_inventory.Current?.Apps ?? [], _games, CloudSync.Roots());
        var rows = new List<BigFileRow>();
        foreach (var chip in FileDriveChips.Where(c => c.IsSelected))
        {
            if (!_scans.TryGetValue(chip.Key, out var r)) continue;
            foreach (var f in r.Largest.Concat(r.OldLarge).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (_goneFiles.Contains(f.Path)) continue;
                rows.Add(new BigFileRow(f, Path.GetFileName(f.Path), Path.GetDirectoryName(f.Path) ?? "", chip.Key, FileKinds.Of(f.Path),
                    owners.Classify(f.Path), f.LastWrite.Year >= 1990 ? f.LastWrite : null));
            }
        }
        _fileRows = rows;
        ApplyFileFilters();
    }

    private void ApplyFileFilters()
    {
        IEnumerable<BigFileRow> q = _fileRows;
        if (FileSearch.Trim() is { Length: > 0 } text)
            q = q.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Folder.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (FileAge.Value > 0) q = q.Where(r => r.Touched is { } t && (DateTime.Now - t).TotalDays >= FileAge.Value);
        if (FileMinSize.Value > 0) q = q.Where(r => r.Bytes >= FileMinSize.Value);
        q = FileOwner.Value switch
        {
            "safe" => q.Where(r => r.Owner.Kind == OwnerKind.Free),
            "cloud" => q.Where(r => r.Owner.Kind == OwnerKind.Synced),
            "appdata" => q.Where(r => r.Owner.Kind == OwnerKind.AppData),
            "apps" => q.Where(r => r.Owner.Kind == OwnerKind.InstalledApp),
            "system" => q.Where(r => r.Owner.Kind == OwnerKind.System),
            _ => q,
        };

        // Type-chip counts reflect every other filter, so they say what clicking them would show.
        var beforeKind = q.ToList();
        var counts = beforeKind.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count());
        foreach (var c in FileKindChips)
        {
            var n = c.Key == "all" ? beforeKind.Count : Enum.TryParse<FileKind>(c.Key, out var k) ? counts.GetValueOrDefault(k) : 0;
            c.Detail = n.ToString("N0");
            c.IsAvailable = n > 0 || c.IsSelected;
        }
        var shown = _kindFilter is { } kind ? beforeKind.Where(r => r.Kind == kind) : beforeKind;

        Func<BigFileRow, object> key = FileSortKey switch
        {
            "AgeDays" => r => r.AgeDays,
            "Name" => r => r.Name,
            "Folder" => r => r.Folder,
            "Drive" => r => r.Drive,
            "OwnerText" => r => r.OwnerText,
            "KindText" => r => r.KindText,
            _ => r => r.Bytes,
        };
        IComparer<object> cmp = Comparer<object>.Create((a, b) => a is string sa && b is string sb
            ? StringComparer.CurrentCultureIgnoreCase.Compare(sa, sb)
            : Comparer<object>.Default.Compare(a, b));
        var ordered = (FileSortDescending ? shown.OrderByDescending(key, cmp) : shown.OrderBy(key, cmp)).ThenByDescending(r => r.Bytes).ToList();
        Files.ReplaceAll(ordered);

        HasFileFilters = FileSearch.Trim().Length > 0 || FileAge.Value > 0 || FileMinSize.Value > 0 || FileOwner.Value != "any" || _kindFilter is not null;
        var active = (FileAge.Value > 0 ? 1 : 0) + (FileMinSize.Value > 0 ? 1 : 0) + (FileOwner.Value != "any" ? 1 : 0) + (_kindFilter is not null ? 1 : 0);
        FiltersButtonText = active > 0 ? $"Filters · {active}" : "Filters";
        var drives = FileDriveChips.Where(c => c.IsSelected).Select(c => c.Key).ToList();
        if (drives.Count == 0)
        {
            FilesSummary = FileDriveChips.Any(c => c.IsAvailable) ? "No drive selected — pick one above." : "Scan a drive on the Explore tab first (or click a drive above).";
            return;
        }
        var safe = ordered.Where(r => r.Owner.Kind == OwnerKind.Free).Sum(r => r.Bytes);
        var cloud = ordered.Where(r => r.CanFreeUp).Sum(r => r.Bytes);
        FilesSummary = (HasFileFilters ? $"Showing {ordered.Count:N0} of {_fileRows.Count:N0} files" : $"{ordered.Count:N0} biggest files")
                       + $" on {string.Join(", ", drives)} · {Format.Bytes(ordered.Sum(r => r.Bytes))}"
                       + (safe > 0 ? $" · {Format.Bytes(safe)} safe to recycle" : "")
                       + (cloud > 0 ? $" · {Format.Bytes(cloud)} can be freed up (stays in the cloud)" : "");
    }

    [RelayCommand]
    private void ResetFileFilters()
    {
        FileSearch = "";
        FileAge = FileAgeOptions[0];
        FileOwner = FileOwnerOptions[0];
        FileMinSize = FileSizeOptions[0];
        FileKindChips[0].IsSelected = true;
    }

    // ---------- selection and actions ----------

    public void SetSelectedFiles(IEnumerable<BigFileRow> rows)
    {
        _selectedFiles = rows.ToList();
        SelectedFileCount = _selectedFiles.Count;
        RecycleSelectedCommand.NotifyCanExecuteChanged(); // same count, different rows
        FreeUpSelectedCommand.NotifyCanExecuteChanged();
        FileSelectionSummary = _selectedFiles.Count == 0 ? null
            : $"{_selectedFiles.Count} selected · {Format.Bytes(_selectedFiles.Sum(r => r.Bytes))}"
              + (_selectedFiles.Count(r => r.CanFreeUp) is var c and > 0 ? $" · {c} in the cloud" : "")
              + (_selectedFiles.Count(r => !r.CanAct) is var n and > 0 ? $" · {n} can't be removed here" : "");
    }

    private bool HasSelection() => SelectedFileCount > 0;
    private bool CanRecycleSelection() => _selectedFiles.Any(r => r.CanRecycle);
    private bool CanFreeUpSelection() => _selectedFiles.Any(r => r.CanFreeUp);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ShowSelected() => Explore(_selectedFiles.FirstOrDefault()?.File.Path);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopySelectedPath() => CopyText(string.Join(Environment.NewLine, _selectedFiles.Select(r => r.File.Path)));

    /// <summary>The row button: free up a cloud file, otherwise recycle it.</summary>
    [RelayCommand]
    private Task FileActionAsync(BigFileRow? row) =>
        row is null ? Task.CompletedTask : row.CanFreeUp ? FreeUpAsync([row]) : RecycleAsync([row]);

    [RelayCommand(CanExecute = nameof(CanRecycleSelection))]
    private Task RecycleSelectedAsync() => RecycleAsync(_selectedFiles);

    [RelayCommand(CanExecute = nameof(CanFreeUpSelection))]
    private Task FreeUpSelectedAsync() => FreeUpAsync(_selectedFiles);

    private async Task RecycleAsync(IReadOnlyList<BigFileRow> rows)
    {
        var blocked = rows.Where(r => !r.CanRecycle).ToList();
        var candidates = rows.Where(r => r.CanRecycle).ToList();
        if (candidates.Count == 0)
        {
            await Dialogs.InfoAsync(blocked.Count == 1 ? $"{blocked[0].Owner.Text}.\n\n{blocked[0].File.Path}" : "None of the selected files can be recycled here (they belong to an app, a game or Windows).");
            return;
        }
        var inUse = await Task.Run(() => candidates.Where(r => FileOwnership.IsInUse(r.File.Path)).ToList());
        candidates = candidates.Except(inUse).ToList();
        if (candidates.Count == 0)
        {
            await Dialogs.InfoAsync("A program has the selected file(s) open right now, so they can't be recycled.");
            return;
        }

        var lines = new List<string>();
        lines.Add(candidates.Count == 1 ? candidates[0].File.Path : $"{candidates.Count} files");
        lines.Add(Format.Bytes(candidates.Sum(r => r.Bytes)));
        var cloud = candidates.Where(r => r.CanFreeUp).ToList();
        if (cloud.Count > 0)
            lines.Add($"\n⚠ {cloud.Count} {(cloud.Count == 1 ? "is" : "are")} synced to the cloud: recycling deletes {(cloud.Count == 1 ? "it" : "them")} from the cloud too. Use Free up space to keep {(cloud.Count == 1 ? "it" : "them")} online instead.");
        if (candidates.Any(r => r.Owner.Kind == OwnerKind.AppData))
            lines.Add($"\n⚠ {candidates.Count(r => r.Owner.Kind == OwnerKind.AppData)} belong to app data: {candidates.First(r => r.Owner.Kind == OwnerKind.AppData).Owner.Text}.");
        if (blocked.Count + inUse.Count > 0) lines.Add($"\n{blocked.Count + inUse.Count} other selected file(s) will be left alone (in use, or part of an app or Windows).");
        var drives = string.Join(", ", candidates.Select(r => r.Drive).Distinct());
        lines.Add($"\nThe Recycle Bin is per drive: the space on {drives} is only freed when you empty it (Cleanup page).");
        if (!await Dialogs.ConfirmAsync(string.Join("\n", lines), "Send to the Recycle Bin", title: "Send to the Recycle Bin?", danger: true))
            return;

        var paths = candidates.Select(r => r.File.Path).ToList();
        var ok = await Task.Run(() => RecycleBin.Recycle(paths));
        var gone = paths.Where(p => !File.Exists(p)).ToList();
        Forget(gone);
        FilesNotice = gone.Count == paths.Count
            ? $"Recycled {gone.Count} file(s), {Format.Bytes(candidates.Sum(r => r.Bytes))}. Empty the Recycle Bin (Cleanup page) to get the space back."
            : $"Recycled {gone.Count} of {paths.Count}; Windows refused the rest (in use, or access denied).";
        if (!ok && gone.Count == 0) await Dialogs.InfoAsync("Windows couldn't move them to the Recycle Bin (in use, or access denied).");
    }

    /// <summary>Makes synced files online-only. Nothing is deleted; the provider drops the local copy once
    /// the cloud has it (and keeps it if the upload hasn't finished).</summary>
    private async Task FreeUpAsync(IReadOnlyList<BigFileRow> rows)
    {
        var cloud = rows.Where(r => r.CanFreeUp).ToList();
        if (cloud.Count == 0)
        {
            await Dialogs.InfoAsync("Free up space works for files in a cloud-synced folder (iCloud Drive, Proton Drive, OneDrive…). None of the selected files are.");
            return;
        }
        var provider = cloud.Select(r => r.Owner.Provider ?? "The cloud app").Distinct().ToList();
        var bytes = cloud.Sum(r => r.Bytes);
        if (!await Dialogs.ConfirmAsync(
                $"Make {(cloud.Count == 1 ? cloud[0].Name : $"{cloud.Count} files")} online-only to free up {Format.Bytes(bytes)}?\n\n"
                + $"{string.Join(" and ", provider)} keeps {(cloud.Count == 1 ? "it" : "them")}; only the copy on this disk goes. Opening one later downloads it again.",
                "Free up space", title: "Free up space?"))
            return;

        FilesNotice = $"Asking {string.Join(" and ", provider)} to free up {Format.Bytes(bytes)}…";
        var requested = await Task.Run(() => cloud.Count(r =>
        {
            try { return CloudSync.RequestFreeUp(r.File.Path); }
            catch { return false; }
        }));

        // Providers free space in the background; give them a few seconds before reporting.
        var freed = new List<BigFileRow>();
        for (var i = 0; i < 10 && freed.Count < cloud.Count; i++)
        {
            await Task.Delay(1000);
            freed = await Task.Run(() => cloud.Where(r => CloudSync.OnDiskBytes(r.File.Path) == 0).ToList());
        }
        Forget(freed.Select(r => r.File.Path).ToList());
        FilesNotice = freed.Count == cloud.Count
            ? $"Freed {Format.Bytes(freed.Sum(r => r.Bytes))} — {(cloud.Count == 1 ? "the file is" : "the files are")} online-only now."
            : requested == 0
                ? "The cloud app didn't accept the request. Try Explorer › right-click › Free up space, or check that it's running."
                : $"Freed {Format.Bytes(freed.Sum(r => r.Bytes))} so far; {string.Join(" and ", provider)} is still working on {cloud.Count - freed.Count} file(s) (it waits until they're fully uploaded).";
    }

    /// <summary>Drops files that are gone (recycled, freed up) from the list and from the saved scans.</summary>
    private void Forget(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        foreach (var p in paths) _goneFiles.Add(p);
        foreach (var drive in paths.Select(DriveOf).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_scans.TryGetValue(drive, out var r)) continue;
            var updated = new ScanResult
            {
                Root = r.Root,
                Largest = r.Largest.Where(f => !_goneFiles.Contains(f.Path)).ToList(),
                OldLarge = r.OldLarge.Where(f => !_goneFiles.Contains(f.Path)).ToList(),
                Elapsed = r.Elapsed,
                When = r.When,
                DriveUsedBytes = r.DriveUsedBytes,
                Method = r.Method,
                FromCache = r.FromCache,
                OnlineOnlyBytes = r.OnlineOnlyBytes,
            };
            _scans[drive] = updated;
            ScanCache.Save(updated);
        }
        _fileRows = _fileRows.Where(r => !_goneFiles.Contains(r.File.Path)).ToList();
        ApplyFileFilters();
        RefreshDrives();
    }
}
