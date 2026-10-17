using System.Collections.ObjectModel;
using System.IO;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Core.Data;
using CarsControl.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CarsControl.App.ViewModels;

public sealed partial class DupFileRow(DuplicateFile file, DupGroupRow group) : ObservableObject
{
    public DuplicateFile File { get; } = file;
    public string Path => File.Path;
    public string Folder => System.IO.Path.GetDirectoryName(File.Path) ?? "";
    public string Name => System.IO.Path.GetFileName(File.Path);
    public string Modified => File.Modified.ToString("d MMM yyyy, h:mm tt");
    [ObservableProperty] private bool _keep;
    public override string ToString() => $"{Name} in {Folder}, {(Keep ? "kept" : "will be recycled")}";

    partial void OnKeepChanged(bool value)
    {
        if (value) group.KeepOnly(this);
    }

    [RelayCommand]
    private void Show() => Platform.ShowInFileManager(File.Path);
}

public sealed partial class DupGroupRow : ObservableObject
{
    private bool _updating;

    public DupGroupRow(DuplicateGroup group, DuplicatesViewModel owner)
    {
        Group = group;
        Owner = owner;
        Files = new ObservableCollection<DupFileRow>(group.Files.Select(f => new DupFileRow(f, this)));
    }

    public DuplicateGroup Group { get; }
    public DuplicatesViewModel Owner { get; }
    public ObservableCollection<DupFileRow> Files { get; }
    public string Title => System.IO.Path.GetFileName(Group.Files[0].Path);
    public string Detail => $"{Group.Files.Count} copies of {Format.Bytes(Group.Bytes)}: {Format.Bytes(Group.Wasted)} wasted";
    public override string ToString() => $"{Title}: {Detail}";

    public IEnumerable<DuplicateFile> Extras => Files.Where(f => !f.Keep).Select(f => f.File);
    public DuplicateFile? Kept => Files.FirstOrDefault(f => f.Keep)?.File;

    internal void Apply(KeepRule rule, string? folder)
    {
        var (keep, _) = DuplicateRules.Choose(Group, rule, folder);
        _updating = true;
        foreach (var f in Files) f.Keep = ReferenceEquals(f.File, keep);
        _updating = false;
        Owner.Recount();
    }

    internal void KeepOnly(DupFileRow chosen)
    {
        if (_updating) return;
        _updating = true;
        foreach (var f in Files.Where(f => f != chosen)) f.Keep = false;
        _updating = false;
        Owner.Recount();
    }
}

public sealed record RuleChoice(string Label, KeepRule Rule)
{
    public override string ToString() => Label;
}

/// <summary>
/// Storage › Duplicates: pick folders, find files with identical content, choose which copy to keep, and send the rest to
/// the Recycle Bin. Files that belong to an installed app, a game or a vault, online-only cloud files and anything on a
/// Windows or program folder are never looked at.
/// </summary>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    private readonly InventoryStore _inventory;
    private readonly CarsDb _db;
    private CancellationTokenSource? _cts;

    public ObservableCollection<DupGroupRow> Groups { get; } = [];
    public ObservableCollection<SelectableFolder> Folders { get; } = [];
    public IReadOnlyList<RuleChoice> Rules { get; } = [new("Keep the newest copy", KeepRule.Newest), new("Keep the oldest copy", KeepRule.Oldest), new("Keep the one with the shortest path", KeepRule.ShortestPath), new("Keep the copy in a folder I choose", KeepRule.InFolder)];
    public IReadOnlyList<int> MinSizes { get; } = [1, 10, 100, 1000];

    [ObservableProperty] private RuleChoice _rule;
    [ObservableProperty] private string? _keepFolder;
    [ObservableProperty] private int _minMegabytes = 1;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Choose folders, then Find duplicates.";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;
    [ObservableProperty] private int _extraCount;
    [ObservableProperty] private long _extraBytes;

    public DuplicatesViewModel(InventoryStore inventory, CarsDb db)
    {
        _inventory = inventory;
        _db = db;
        _rule = Rules[0];
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var (path, label, on) in new[]
        {
            (DownloadsFolder(), "Downloads", true), (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents", true), (Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pictures", true),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Videos", false), (Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Music", false), (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop", true),
        })
            if (Directory.Exists(path)) Folders.Add(new SelectableFolder(path, label) { IsSelected = on });
        _ = profile;
    }

    public static string DownloadsFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (k?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string raw && Environment.ExpandEnvironmentVariables(raw) is { Length: > 3 } path && Directory.Exists(path)) return path;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException) { /* use the default */ }
        return Path.Combine(profile, "Downloads");
    }

    public bool ShowKeepFolder => Rule.Rule == KeepRule.InFolder;

    partial void OnRuleChanged(RuleChoice value)
    {
        OnPropertyChanged(nameof(ShowKeepFolder));
        if (value.Rule == KeepRule.InFolder && KeepFolder is null && Platform.PickFolder("Keep the copy in which folder?") is { } f) KeepFolder = f;
        ApplyRule();
    }

    [RelayCommand]
    private void AddFolder()
    {
        if (Platform.PickFolder("Add a folder or drive to search for duplicates") is { } p && Folders.All(f => !f.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
            Folders.Add(new SelectableFolder(p, p) { IsSelected = true });
    }

    [RelayCommand]
    private void ChooseKeepFolder()
    {
        if (Platform.PickFolder("Keep the copy in which folder?", KeepFolder) is { } f)
        {
            KeepFolder = f;
            ApplyRule();
        }
    }

    // ---------- scanning ----------

    [RelayCommand]
    private async Task FindAsync()
    {
        var chosen = Folders.Where(f => f.IsSelected).Select(f => f.Path).ToList();
        if (chosen.Count == 0) { Status = "Tick at least one folder."; return; }
        _cts = new CancellationTokenSource();
        IsScanning = true;
        Groups.Clear();
        Banner = null;
        Status = "Starting…";
        try
        {
            var apps = _inventory.Current?.Apps ?? [];
            var games = await Task.Run(() => GameLibraries.Read(apps).Games);
            var ownership = new FileOwnership(apps, games, CloudSync.Roots());
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var skip = new Func<string, bool>(path =>
            {
                if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) || path.Contains(@"\$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\.cars-versions", StringComparison.OrdinalIgnoreCase)
                    || path.Contains(@"\System Volume Information", StringComparison.OrdinalIgnoreCase)) return true;
                return ownership.Classify(path).Kind is OwnerKind.InstalledApp or OwnerKind.System or OwnerKind.InUse;
            });
            var progress = new Progress<DuplicateProgress>(p => Status = p.Stage switch
            {
                "Listing files" => $"Looking through the folders… {p.FilesSeen:N0} files so far",
                "Comparing" => $"Comparing {p.Candidates:N0} files of the same size… {p.Hashed:N0} done",
                _ => $"Reading to be sure… {p.Hashed:N0} of {p.Candidates:N0}",
            });
            var drive = Path.GetPathRoot(chosen[0]) ?? "C:\\";
            var found = await DuplicateFinder.FindAsync(new DuplicateOptions(chosen, MinMegabytes * 1_048_576L, skip, Math.Max(1, DriveKind.ScanParallelism(drive) / 2)), progress, _cts.Token);
            foreach (var g in found.Take(200)) Groups.Add(new DupGroupRow(g, this));
            ApplyRule();
            Status = found.Count == 0 ? "No duplicates found in those folders." : $"{found.Count:N0} sets of duplicates" + (found.Count > 200 ? " (the 200 biggest are listed)" : "") + $", wasting {Format.Bytes(found.Sum(g => g.Wasted))} in all.";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        finally
        {
            IsScanning = false;
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelFind() => _cts?.Cancel();

    private void ApplyRule()
    {
        foreach (var g in Groups) g.Apply(Rule.Rule, KeepFolder);
        Recount();
    }

    internal void Recount()
    {
        ExtraCount = Groups.Sum(g => g.Extras.Count());
        ExtraBytes = Groups.Sum(g => g.Group.Bytes * g.Extras.Count());
        Summary = Groups.Count == 0 ? "" : $"{ExtraCount:N0} copies would be recycled, freeing {Format.Bytes(ExtraBytes)}. Keep one per set; you can change which with the buttons.";
    }

    // ---------- recycling ----------

    [RelayCommand]
    private async Task RecycleAsync()
    {
        if (ExtraCount == 0) return;
        if (!await Dialogs.ConfirmAsync($"Send {ExtraCount:N0} duplicate files ({Format.Bytes(ExtraBytes)}) to the Recycle Bin?\n\nOne copy of each stays where it is. Nothing is deleted for good: the Recycle Bin can put them back until you empty it (and on D: or A: it keeps them there, so those drives only gain space when it's emptied).", "Recycle", danger: true)) return;
        IsBusy = true;
        try
        {
            var toRecycle = new List<string>();
            var skipped = 0;
            foreach (var g in Groups)
            {
                if (g.Kept is not { } keep) { skipped += g.Extras.Count(); continue; } // never recycle a set with no kept copy
                foreach (var extra in g.Extras)
                {
                    if (DuplicateRules.StillSafeToRecycle(extra, keep)) toRecycle.Add(extra.Path);
                    else skipped++;
                }
            }
            var ok = await Task.Run(() => RecycleBin.Recycle(toRecycle));
            _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "cleanup", "duplicates", "Duplicate files", "Storage", null, null, false, ok ? "Succeeded" : "Failed", ok ? 0 : 1,
                $"Recycled {toRecycle.Count:N0} duplicate files" + (skipped > 0 ? $", skipped {skipped} that had changed" : ""), null));
            Banner = ok ? $"Recycled {toRecycle.Count:N0} files." + (skipped > 0 ? $" {skipped} were skipped because they changed since the search." : "") : "Windows couldn't recycle some of the files.";
            BannerKind = ok ? NoticeKind.Success : NoticeKind.Warning;
            var recycled = toRecycle.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var g in Groups.ToList())
            {
                foreach (var f in g.Files.Where(f => recycled.Contains(f.Path)).ToList()) g.Files.Remove(f);
                if (g.Files.Count < 2) Groups.Remove(g);
            }
            Recount();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed partial class SelectableFolder(string path, string label) : ObservableObject
{
    public string Path { get; } = path;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
    public override string ToString() => Label;
}
