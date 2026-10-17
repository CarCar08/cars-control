using System.Collections.ObjectModel;
using System.IO;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Core.Data;
using CarsControl.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class JunkItemRow(JunkItem item, JunkViewModel owner) : ObservableObject
{
    public JunkItem Item { get; } = item;
    public string Name => Item.Name;
    public string Path => Item.Path;
    public string Reason => Item.Reason;
    public string SizeText => Item.IsFolder && Item.Bytes == 0 ? "empty" : Format.Bytes(Item.Bytes);
    public string Modified => Item.Modified.ToString("d MMM yyyy");
    [ObservableProperty] private bool _isSelected = item.Kind != JunkKind.HugeLog;
    public override string ToString() => $"{Name}, {SizeText}";

    partial void OnIsSelectedChanged(bool value) => owner.Recount();

    [RelayCommand]
    private void Show() => Platform.ShowInFileManager(Item.Path);
}

public sealed partial class JunkGroupRow : ObservableObject
{
    public const int ShownMax = 150;
    private bool _syncing;

    public JunkGroupRow(string category, string help, IEnumerable<JunkItemRow> items)
    {
        Category = category;
        Help = help;
        All = items.ToList();
        Items = new ObservableCollection<JunkItemRow>(All.Take(ShownMax));
        _groupSelected = All.Count > 0 && All.All(i => i.IsSelected);
    }

    public string Category { get; }
    public string Help { get; }

    /// <summary>Everything found. A long list (thousands of empty folders) would freeze the window if every row were drawn, so only the first
    /// few are listed; ticking or unticking the group still applies to all of them.</summary>
    public List<JunkItemRow> All { get; }

    public ObservableCollection<JunkItemRow> Items { get; }
    public int HiddenCount => Math.Max(0, All.Count - Items.Count);
    public string HiddenText => HiddenCount == 0 ? "" : $"…and {HiddenCount:N0} more like these (the box above covers them too).";
    public string Detail => $"{All.Count:N0} item{(All.Count == 1 ? "" : "s")} · {Format.Bytes(All.Sum(i => i.Item.Bytes))}";
    public override string ToString() => $"{Category}: {Detail}";

    [ObservableProperty] private bool _groupSelected;

    partial void OnGroupSelectedChanged(bool value)
    {
        if (_syncing) return;
        _syncing = true;
        foreach (var i in All) i.IsSelected = value;
        _syncing = false;
    }

    internal void Remove(HashSet<string> gone)
    {
        All.RemoveAll(i => gone.Contains(i.Path));
        foreach (var i in Items.Where(i => gone.Contains(i.Path)).ToList()) Items.Remove(i);
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HiddenText));
        OnPropertyChanged(nameof(Detail));
    }
}

/// <summary>
/// Storage › Junk: old installers in Downloads, folders with nothing in them, logs that grew huge and the setup files Steam
/// leaves inside games. Everything found is listed first; what you tick goes to the Recycle Bin, so it can be put back.
/// </summary>
public sealed partial class JunkViewModel : ObservableObject
{
    private readonly CarsDb _db;
    private CancellationTokenSource? _cts;

    public ObservableCollection<JunkGroupRow> Groups { get; } = [];

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Press Look for junk to see what's there. Nothing is removed until you tick it and confirm.";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private long _selectedBytes;

    public JunkViewModel(CarsDb db) => _db = db;

    public bool HasSelection => SelectedCount > 0;

    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(HasSelection));

    private static string Help(JunkKind k) => k switch
    {
        JunkKind.Installer => "Setup programs you downloaded two months ago or more. If you ever need one again, download it again.",
        JunkKind.EmptyFolder => "Folders in Documents, Downloads, Pictures and the like with nothing inside, made over a week ago.",
        JunkKind.HugeLog => "Log files over 100 MB that aren't being written to. The program starts a new one if it needs it. Not ticked: look first.",
        _ => "Visual C++ and DirectX setup files Steam leaves inside games. They've already run; Steam puts them back if a game needs them.",
    };

    [RelayCommand]
    private async Task FindAsync()
    {
        _cts = new CancellationTokenSource();
        IsScanning = true;
        Groups.Clear();
        Banner = null;
        Status = "Looking…";
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var options = new JunkOptions(
                DuplicatesViewModel.DownloadsFolder(),
                new[] { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), DuplicatesViewModel.DownloadsFolder(), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) }
                    .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)],
                GameLibraries.SteamLibraryRoots().Where(Directory.Exists).ToList());
            var items = await Task.Run(() => JunkFinder.Find(options, _cts.Token));
            foreach (var g in items.GroupBy(i => i.Kind).OrderByDescending(g => g.Sum(i => i.Bytes)))
                Groups.Add(new JunkGroupRow(g.First().Category, Help(g.Key), g.Select(i => new JunkItemRow(i, this))));
            Recount();
            Status = items.Count == 0 ? "Nothing like that found." : $"{items.Count:N0} items found, {Format.Bytes(items.Sum(i => i.Bytes))} in all.";
            _ = profile;
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

    internal void Recount()
    {
        var chosen = Groups.SelectMany(g => g.All).Where(i => i.IsSelected).ToList();
        SelectedCount = chosen.Count;
        SelectedBytes = chosen.Sum(i => i.Item.Bytes);
        Summary = chosen.Count == 0 ? "" : $"{chosen.Count:N0} ticked, {Format.Bytes(SelectedBytes)}.";
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in Groups.SelectMany(g => g.All)) i.IsSelected = false;
    }

    [RelayCommand]
    private async Task RecycleAsync()
    {
        var chosen = Groups.SelectMany(g => g.All).Where(i => i.IsSelected).ToList();
        if (chosen.Count == 0) return;
        if (!await Dialogs.ConfirmAsync($"Send {chosen.Count:N0} items ({Format.Bytes(SelectedBytes)}) to the Recycle Bin?\n\nThey stay there until you empty it, so you can put any of them back.", "Recycle", danger: true)) return;
        IsBusy = true;
        try
        {
            // Folders and files are checked again right before: a file replaced since the search isn't touched.
            var paths = chosen.Where(i => i.Item.IsFolder ? Directory.Exists(i.Path) : File.Exists(i.Path) && new FileInfo(i.Path).Length == i.Item.Bytes).Select(i => i.Path).ToList();
            var ok = await Task.Run(() => RecycleBin.Recycle(paths));
            _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "cleanup", "junk", "Junk files and folders", "Storage", null, null, false, ok ? "Succeeded" : "Failed", ok ? 0 : 1,
                $"Recycled {paths.Count:N0} items ({Format.Bytes(SelectedBytes)})", string.Join(Environment.NewLine, paths.Take(200))));
            Banner = ok ? $"Recycled {paths.Count:N0} items." : "Windows couldn't recycle some of them.";
            BannerKind = ok ? NoticeKind.Success : NoticeKind.Warning;
            var gone = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var g in Groups.ToList())
            {
                g.Remove(gone);
                if (g.All.Count == 0) Groups.Remove(g);
            }
            Recount();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed partial class MovedFolderRow(LinkedMoveRecordView record, MoveFoldersViewModel owner) : ObservableObject
{
    public LinkedMoveRecordView Record { get; } = record;
    public string Original => Record.Original;
    public string NewLocation => Record.NewLocation;
    public string Detail => $"{Format.Bytes(Record.Bytes)}, moved {Format.Ago(Record.When)}";
    public override string ToString() => $"{Original} is a link to {NewLocation}";
    [ObservableProperty] private bool _isBusy;
    [RelayCommand] private Task MoveBackAsync() => owner.MoveBackAsync(this);
    [RelayCommand] private void Show() => Platform.ShowInFileManager(Record.NewLocation);
}

public sealed record LinkedMoveRecordView(string Original, string NewLocation, DateTime When, long Bytes);

/// <summary>Storage › Move folders: send a big folder to another drive and leave a link behind, and undo it later.</summary>
public sealed partial class MoveFoldersViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public ObservableCollection<MovedFolderRow> Moved { get; } = [];

    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _stage = "";
    [ObservableProperty] private bool _running;

    public MoveFoldersViewModel(AppSettings settings) => _settings = settings;

    public void Refresh()
    {
        Moved.Clear();
        foreach (var m in _settings.Store.Current.Storage.LinkedMoves.OrderByDescending(m => m.When))
            Moved.Add(new MovedFolderRow(new LinkedMoveRecordView(m.Original, m.NewLocation, m.When, m.Bytes), this));
    }

    [RelayCommand]
    private void Pick()
    {
        if (Platform.PickFolder("Choose the folder to move to another drive") is not { } folder) return;
        // Ask the rules first so the window never opens for a folder that can't be moved.
        var otherDrive = DriveInfo.GetDrives().FirstOrDefault(d => d.DriveType == DriveType.Fixed && d.IsReady && !d.Name.Equals(Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase));
        if (otherDrive is null) { Notice("There's no other drive to move it to.", NoticeKind.Warning); return; }
        if (FolderLinkMover.Refuse(folder, otherDrive.Name) is { } why) { Notice(why, NoticeKind.Warning); return; }
        var changed = Platform.ShowMove(MoveViewModel.ForFolder(folder, _settings.Store, () => { }));
        Refresh();
        if (changed) Notice("Done. The folder now lives on the other drive, and the old path leads there.", NoticeKind.Success);
    }

    internal async Task MoveBackAsync(MovedFolderRow row)
    {
        if (!await Dialogs.ConfirmAsync($"Move {row.Original} back from {row.NewLocation}?\n\nThe link is replaced by the folder itself again.", "Move back")) return;
        row.IsBusy = true;
        Running = true;
        try
        {
            var progress = new Progress<MoveProgress>(p => { Percent = p.Percent; Stage = $"{p.Stage} · {Format.Bytes(p.BytesDone)} of {Format.Bytes(p.BytesTotal)}"; });
            var result = await FolderLinkMover.MoveBackAsync(new LinkedMove(row.Original, row.NewLocation, row.Record.When, row.Record.Bytes), progress, CancellationToken.None);
            if (result.Ok) _settings.Store.Update(s => s.Storage.LinkedMoves.RemoveAll(m => m.Original.Equals(row.Original, StringComparison.OrdinalIgnoreCase)));
            Notice(result.Message, result.Ok ? NoticeKind.Success : NoticeKind.Error);
        }
        finally
        {
            row.IsBusy = false;
            Running = false;
            Refresh();
        }
    }

    private void Notice(string text, NoticeKind kind)
    {
        Banner = text;
        BannerKind = kind;
    }
}
