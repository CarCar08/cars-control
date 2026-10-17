using CarsControl.App.Services;
using CarsControl.App.Services.Palette;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CarsControl.App.ViewModels;

/// <summary>The command palette: type, ↑↓ to move, Enter to run, → for more actions, Esc to close.</summary>
public sealed partial class PaletteViewModel : ObservableObject
{
    private readonly PaletteService _service;
    private List<PaletteItem> _items;

    public BulkObservableCollection<PaletteRow> Rows { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteRow? _selected;

    /// <summary>Set while the extra actions of one result are shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(Placeholder))]
    private PaletteItem? _moreFor;

    [ObservableProperty] private bool _isEmpty;

    public string Placeholder => MoreFor is { } m ? $"Actions for {m.Title}" : "Type a command, page, app, game, file or setting…";

    public string Hint => MoreFor is null
        ? "↑↓ move · Enter run · → more actions · Esc close"
        : "↑↓ move · Enter run · ← back · Esc back";

    /// <summary>The palette asks its window to close (after Enter, or Esc).</summary>
    public event Action? CloseRequested;

    public PaletteViewModel(PaletteService service)
    {
        _service = service;
        _items = service.Build();
        Refresh();
        // Games and big files come from the saved scans; when those finish loading while the palette is
        // open, search again so they show up.
        if (_service.ScansPending)
            _service.WhenScansLoaded(() =>
            {
                _items = _service.Build();
                if (MoreFor is null) Refresh();
            });
    }

    partial void OnQueryChanged(string value) => Refresh();

    private void Refresh()
    {
        if (MoreFor is { } item)
        {
            var q = Query.Trim();
            var rows = item.More
                .Where(m => q.Length == 0 || Core.Palette.FuzzyMatcher.Score(q, m.Title) is not null)
                .Select(m => new PaletteRow(m, null));
            Rows.ReplaceAll(rows.Prepend(new PaletteRow(null, item.Title)));
        }
        else
        {
            Rows.ReplaceAll(_service.Search(Query, _items));
        }
        IsEmpty = !Rows.Any(r => !r.IsHeader);
        Selected = Rows.FirstOrDefault(r => !r.IsHeader);
    }

    public void Move(int delta)
    {
        var items = Rows.Where(r => !r.IsHeader).ToList();
        if (items.Count == 0) return;
        var i = Selected is null ? -1 : items.IndexOf(Selected);
        Selected = items[Math.Clamp(i + delta, 0, items.Count - 1)];
    }

    /// <summary>→: the selected result's extra actions (if it has any).</summary>
    public bool ShowMore()
    {
        if (Selected?.Item is not { HasMore: true } item) return false;
        MoreFor = item;
        Query = "";
        Refresh();
        return true;
    }

    /// <summary>←/Esc out of the extra actions; false when already at the top level.</summary>
    public bool Back()
    {
        if (MoreFor is null) return false;
        MoreFor = null;
        Query = "";
        Refresh();
        return true;
    }

    public async Task RunAsync(PaletteRow? row = null)
    {
        if ((row ?? Selected)?.Item is not { } item) return;
        _service.Used(MoreFor ?? item);
        CloseRequested?.Invoke(); // first, so dialogs the action opens belong to the main window
        try
        {
            await item.Run();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"palette {item.Id}", ex);
            await Dialogs.InfoAsync($"That didn't work: {ex.Message}");
        }
    }
}
