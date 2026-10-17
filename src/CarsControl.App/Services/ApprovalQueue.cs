using System.Text.Json;
using CarsControl.Core.Cleanup;
using CarsControl.Core.Data;
using CarsControl.Core.Updates;

namespace CarsControl.App.Services;

/// <summary>Admin work that waited because nobody was there to approve it. Updates are re-matched to
/// the latest check when approved, so an item that was updated some other way in the meantime is dropped.</summary>
public sealed record ApprovalItem(string Kind, string Key, string Title, string Origin, DateTime Added)
{
    public const string Update = "update";
    public const string Cleanup = "cleanup";
}

/// <summary>
/// The approval queue: admin parts of scheduled maintenance (and of "after you play") that wait for one
/// prompt when you're back. Kept in cars.db: it's state of this PC, not a preference.
/// </summary>
public sealed class ApprovalQueue
{
    private const string SettingKey = "approval_queue_v1";
    private readonly CarsDb _db;
    private List<ApprovalItem> _items = [];

    /// <summary>CARS_DEV_FAKE_APPROVALS=1 in a test copy: sample items for screenshots, never saved.</summary>
    private readonly bool _fake = App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEV_FAKE_APPROVALS") == "1";

    public ApprovalQueue(CarsDb db)
    {
        _db = db;
        try
        {
            if (_db.GetSetting(SettingKey) is { Length: > 0 } json) _items = JsonSerializer.Deserialize<List<ApprovalItem>>(json) ?? [];
        }
        catch (JsonException)
        {
            _items = [];
        }
        if (_fake)
            _items =
            [
                new(ApprovalItem.Update, "winget:Sample.App", "Sample app", "Scheduled maintenance", DateTime.Now),
                new(ApprovalItem.Update, "wu:sample", "Sample Windows update", "Scheduled maintenance", DateTime.Now),
                new(ApprovalItem.Cleanup, "windows-temp", "Windows temporary files", "Scheduled maintenance", DateTime.Now),
            ];
    }

    public IReadOnlyList<ApprovalItem> Items => _items;
    public int Count => _items.Count;

    /// <summary>Changes whenever the set of waiting items changes (used so a toast is shown once per set).</summary>
    public string Signature => string.Join("|", _items.Select(i => $"{i.Kind}:{i.Key}").Order());

    /// <summary>Raised on the UI thread.</summary>
    public event Action? Changed;

    public void Add(IEnumerable<UpdateCandidate> updates, string origin) =>
        AddCore(updates.Select(u => new ApprovalItem(ApprovalItem.Update, u.Key, u.Title, origin, DateTime.Now)));

    public void Add(IEnumerable<CleanupRule> rules, string origin) =>
        AddCore(rules.Select(r => new ApprovalItem(ApprovalItem.Cleanup, r.Id, r.Title, origin, DateTime.Now)));

    private void AddCore(IEnumerable<ApprovalItem> fresh)
    {
        var added = false;
        foreach (var item in fresh)
        {
            if (_items.Any(i => i.Kind == item.Kind && i.Key == item.Key)) continue;
            _items.Add(item);
            added = true;
        }
        if (added) Save();
    }

    public void Remove(IEnumerable<ApprovalItem> done)
    {
        var set = done.Select(d => (d.Kind, d.Key)).ToHashSet();
        if (_items.RemoveAll(i => set.Contains((i.Kind, i.Key))) > 0) Save();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Save();
    }

    /// <summary>"3 admin items are waiting: 2 updates, 1 cleanup".</summary>
    public string Describe()
    {
        var u = _items.Count(i => i.Kind == ApprovalItem.Update);
        var c = _items.Count(i => i.Kind == ApprovalItem.Cleanup);
        var parts = new List<string>();
        if (u > 0) parts.Add(u == 1 ? "1 update" : $"{u} updates");
        if (c > 0) parts.Add(c == 1 ? "1 cleanup" : $"{c} cleanups");
        return string.Join(", ", parts);
    }

    private void Save()
    {
        if (_fake)
        {
            Platform.OnUiThread(() => Changed?.Invoke());
            return;
        }
        _db.SetSetting(SettingKey, _items.Count == 0 ? null : JsonSerializer.Serialize(_items));
        Platform.OnUiThread(() => Changed?.Invoke());
    }
}
