using System.Collections.ObjectModel;
using System.IO;
using CarsControl.App.Services;
using CarsControl.Core.ContextMenu;
using CarsControl.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class MenuRowViewModel(MenuEntry entry, ContextMenuViewModel owner) : ObservableObject
{
    public MenuEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Detail => string.Join(" · ", new[] { Entry.KindText, Entry.Owner }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string Place => Entry.PlaceText;
    public string? Warning => Entry.Warning;
    public bool IsLocked => Entry.Locked;
    public bool IsOurs => Entry.IsOurs;
    public string? Command => Entry.Command;
    public override string ToString() => $"{Name}, {Place}, {(Shown ? "shown" : "hidden")}";

    [ObservableProperty] private bool _shown = !entry.Hidden;

    internal bool Syncing { get; set; }

    partial void OnShownChanged(bool value)
    {
        if (!Syncing) owner.Change(this, value);
    }
}

/// <summary>
/// Apps › Right-click menu: what's in Explorer's right-click menu and who added it, with a switch each. Hiding is
/// per user and reversible (see <see cref="ContextMenuEntries"/>); Nilesoft Shell, which draws this PC's whole menu,
/// is shown but never switched. Also installs C.A.R.S.'s own "Folder size" and "Scan this drive" entries.
/// </summary>
public sealed partial class ContextMenuViewModel : ObservableObject
{
    private readonly CarsDb _db;
    private IReadOnlyList<MenuEntry> _all = [];

    public ObservableCollection<MenuRowViewModel> Rows { get; } = [];

    public IReadOnlyList<string> PlaceChoices { get; } = ["Everywhere", "Files", "Files and folders", "Folders", "Empty space in a folder", "Drives"];

    [ObservableProperty] private int _placeIndex;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _thirdPartyOnly = true;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;
    [ObservableProperty] private bool _carsEntries;
    private bool _syncingCars;

    public ContextMenuViewModel(CarsDb db) => _db = db;

    /// <summary>Test copies share your registry: they show the menu but change nothing in it.</summary>
    public bool ReadOnly => App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEV_ALLOW_MENU") != "1";

    partial void OnPlaceIndexChanged(int value) => Populate();
    partial void OnFilterChanged(string value) => Populate();
    partial void OnThirdPartyOnlyChanged(bool value) => Populate();

    public void Refresh()
    {
        _all = ContextMenuEntries.Read();
        _syncingCars = true;
        CarsEntries = CarsEntries_IsInstalled();
        _syncingCars = false;
        Populate();
    }

    private static bool CarsEntries_IsInstalled() => Core.ContextMenu.CarsEntries.IsInstalled();

    private void Populate()
    {
        var f = Filter.Trim();
        bool InPlace(MenuEntry e) => PlaceIndex switch
        {
            1 => e.Place == MenuPlace.Files,
            2 => e.Place == MenuPlace.Everything,
            3 => e.Place == MenuPlace.Folders,
            4 => e.Place == MenuPlace.FolderBackground,
            5 => e.Place == MenuPlace.Drives,
            _ => true,
        };
        var shown = _all.Where(e => InPlace(e)
            && (f.Length == 0 || e.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || (e.Owner?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false))
            && (!ThirdPartyOnly || e.Owner is null || !e.Owner.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || e.Hidden)).ToList();
        Rows.Clear();
        foreach (var e in shown) Rows.Add(new MenuRowViewModel(e, this));
        var hidden = _all.Count(e => e.Hidden);
        Summary = $"{_all.Count} right-click items, {hidden} hidden. " + (ThirdPartyOnly ? "Microsoft's own are tucked away; untick the box to see them." : "");
    }

    internal void Change(MenuRowViewModel row, bool shown)
    {
        var entry = row.Entry;
        if (row.IsLocked)
        {
            Revert(row, !shown);
            Notice(entry.Warning ?? "That one isn't changed from here.", NoticeKind.Info);
            return;
        }
        if (ReadOnly)
        {
            Revert(row, !shown);
            Notice("This is a test copy: it doesn't change your real right-click menu.", NoticeKind.Info);
            return;
        }
        try
        {
            if (!ContextMenuEntries.SetHidden(entry, hidden: !shown))
            {
                Revert(row, !shown);
                Notice($"“{entry.Name}” can't be changed from here.", NoticeKind.Warning);
                return;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Revert(row, !shown);
            Notice("Windows refused the change: " + ex.Message, NoticeKind.Error);
            return;
        }
        _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "startup", entry.Id, entry.Name, "Right-click menu", null, null, false, "Succeeded", 0,
            shown ? $"Showed “{entry.Name}” in the right-click menu again" : $"Hid “{entry.Name}” from the right-click menu", null));
        Notice(shown ? $"“{entry.Name}” is back in the right-click menu." : $"“{entry.Name}” is hidden. Switch it on again any time; nothing was uninstalled.", NoticeKind.Success);
        _all = _all.Select(e => e.Id == entry.Id ? e with { Hidden = !shown } : e).ToList();
        Summary = $"{_all.Count} right-click items, {_all.Count(e => e.Hidden)} hidden.";
    }

    private static void Revert(MenuRowViewModel row, bool to)
    {
        row.Syncing = true;
        row.Shown = to;
        row.Syncing = false;
    }

    partial void OnCarsEntriesChanged(bool value)
    {
        if (_syncingCars) return;
        if (ReadOnly)
        {
            _syncingCars = true;
            CarsEntries = !value;
            _syncingCars = false;
            Notice("This is a test copy: it doesn't add anything to your real right-click menu.", NoticeKind.Info);
            return;
        }
        try
        {
            if (value) Core.ContextMenu.CarsEntries.Install(Environment.ProcessPath ?? throw new InvalidOperationException("The app's own path isn't known."));
            else Core.ContextMenu.CarsEntries.Remove();
            _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "startup", "menu:cars", "C.A.R.S. right-click items", "Right-click menu", null, null, false, "Succeeded", 0,
                value ? "Added Folder size and Scan this drive to the right-click menu" : "Removed C.A.R.S.'s right-click items", null));
            Notice(value ? "Right-click a folder or drive: “Folder size in C.A.R.S.” opens it in Storage." : "C.A.R.S.'s right-click items are removed.", NoticeKind.Success);
            Refresh();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            Notice("Couldn't change the right-click menu: " + ex.Message, NoticeKind.Error);
        }
    }

    private void Notice(string text, NoticeKind kind)
    {
        Banner = text;
        BannerKind = kind;
    }
}
