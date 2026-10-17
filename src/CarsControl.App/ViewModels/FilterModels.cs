using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CarsControl.App.ViewModels;

/// <summary>One toggle chip in a filter row (a drive, a file type, a game library). The owning view
/// model listens to <see cref="IsSelected"/> and re-filters.</summary>
public sealed partial class FilterChip(string key, string label) : ObservableObject
{
    public string Key { get; } = key;

    [ObservableProperty] private string _label = label;

    /// <summary>Small grey text after the label: a count, "scanned 2 h ago", "not scanned".</summary>
    [ObservableProperty] private string? _detail;

    [ObservableProperty] private string? _toolTip;
    [ObservableProperty] private bool _isSelected;

    /// <summary>False greys the chip out (a disconnected library); it can still be clicked.</summary>
    [ObservableProperty] private bool _isAvailable = true;

    public override string ToString() => Detail is null ? Label : $"{Label} {Detail}";
}

/// <summary>A sort choice for the "Sort" dropdown. Column headers map to the same keys.</summary>
public sealed record SortOption(string Key, string Label, bool DescendingFirst)
{
    public override string ToString() => Label;
}

/// <summary>A dropdown entry with a typed value.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>ObservableCollection that swaps its whole contents with a single Reset notification, so
/// re-filtering thousands of rows doesn't raise thousands of change events (and the list keeps its
/// scroll position, which assigning a new ItemsSource would lose).</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
