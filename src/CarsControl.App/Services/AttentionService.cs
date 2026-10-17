using System.Collections.ObjectModel;
using System.Globalization;
using Wpf.Ui.Controls;

namespace CarsControl.App.Services;

public enum AttentionLevel { Info, Warning, Critical }

/// <summary>One thing that needs you, shown at the top of Home ("3 admin items are waiting", "Drive A: is
/// almost full"). The primary action does something about it; Snooze hides it for a few days.</summary>
public sealed class AttentionItem
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public AttentionLevel Level { get; init; } = AttentionLevel.Warning;
    public SymbolRegular Icon { get; init; } = SymbolRegular.Info24;
    public string? ActionText { get; init; }
    public Func<Task>? Action { get; init; }

    /// <summary>Instead of Snooze (e.g. "Dismiss" for waiting admin items, which forgets them).</summary>
    public string? SecondaryText { get; init; }
    public System.Action? Secondary { get; init; }

    /// <summary>How long Snooze hides it (until the problem changes; the key includes what changed).</summary>
    public TimeSpan SnoozeFor { get; init; } = TimeSpan.FromDays(3);

    public bool HasAction => Action is not null;
    public string SecondaryLabel => SecondaryText ?? "Snooze";
    public bool IsCritical => Level == AttentionLevel.Critical;
    public override string ToString() => Detail is null ? Title : $"{Title}. {Detail}";
}

/// <summary>
/// "Needs attention": features post items under a key and clear them when the problem is gone; Home lists
/// them, worst first. Snoozing is remembered in settings.json per key (so "Drive A: almost full" can be
/// quiet for a few days, and comes back if it gets worse, because the key changes).
/// </summary>
public sealed class AttentionService(AppSettings settings)
{
    private readonly Dictionary<string, AttentionItem> _items = new();

    public ObservableCollection<AttentionItem> Items { get; } = [];

    /// <summary>Raised on the UI thread after the list changes.</summary>
    public event Action? Changed;

    public void Set(string key, AttentionItem? item) => Platform.OnUiThread(() =>
    {
        if (item is null) _items.Remove(key);
        else _items[key] = item;
        Rebuild();
    });

    public void Clear(string key) => Set(key, null);

    public void Snooze(AttentionItem item)
    {
        settings.SetUi($"snooze.{item.Key}", DateTime.Now.Add(item.SnoozeFor).ToString("o", CultureInfo.InvariantCulture));
        Rebuild();
    }

    private bool Snoozed(AttentionItem item) =>
        settings.GetUi($"snooze.{item.Key}") is { } until
        && DateTime.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) && t > DateTime.Now;

    private void Rebuild()
    {
        var visible = _items.Values.Where(i => !Snoozed(i)).OrderByDescending(i => i.Level).ThenBy(i => i.Title).ToList();
        if (visible.SequenceEqual(Items)) return;
        Items.Clear();
        foreach (var i in visible) Items.Add(i);
        Changed?.Invoke();
    }
}
