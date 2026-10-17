using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CarsControl.App.Controls;

/// <summary>
/// Hands DataGrid header clicks to the view model instead of letting the grid sort its own copy:
/// the view model sorts (and filters) once, the "Sort" dropdown and the header arrows always agree,
/// and the choice can be remembered. A column's SortMemberPath is used as the sort key.
/// <code>&lt;DataGrid controls:GridSort.Key="{Binding FileSortKey}" controls:GridSort.Descending="{Binding FileSortDescending}"&gt;</code>
/// Columns that should sort largest/newest first on the first click set controls:GridSort.DescendingFirst="True".
/// </summary>
public static class GridSort
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(GridSort),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnChanged));

    public static readonly DependencyProperty DescendingProperty = DependencyProperty.RegisterAttached(
        "Descending", typeof(bool), typeof(GridSort),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnChanged));

    public static readonly DependencyProperty DescendingFirstProperty = DependencyProperty.RegisterAttached(
        "DescendingFirst", typeof(bool), typeof(GridSort), new PropertyMetadata(false));

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(GridSort), new PropertyMetadata(false));

    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, string? value) => d.SetValue(KeyProperty, value);
    public static bool GetDescending(DependencyObject d) => (bool)d.GetValue(DescendingProperty);
    public static void SetDescending(DependencyObject d, bool value) => d.SetValue(DescendingProperty, value);
    public static bool GetDescendingFirst(DependencyObject d) => (bool)d.GetValue(DescendingFirstProperty);
    public static void SetDescendingFirst(DependencyObject d, bool value) => d.SetValue(DescendingFirstProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        if (!(bool)grid.GetValue(HookedProperty))
        {
            grid.SetValue(HookedProperty, true);
            grid.Sorting += OnSorting;
            // The grid clears every column's arrow when its items are replaced or reset; put ours back. Watched only while the
            // grid is loaded: AddValueChanged is a strong, app-wide reference that would keep a released page alive.
            var itemsSource = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid));
            EventHandler replaced = (_, _) => grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ShowArrows(grid));
            var watching = false;
            grid.Loaded += (_, _) =>
            {
                if (!watching) { watching = true; itemsSource.AddValueChanged(grid, replaced); }
                ShowArrows(grid);
            };
            grid.Unloaded += (_, _) =>
            {
                if (watching) { watching = false; itemsSource.RemoveValueChanged(grid, replaced); }
            };
            if (grid.IsLoaded) { watching = true; itemsSource.AddValueChanged(grid, replaced); }
            ((INotifyCollectionChanged)grid.Items).CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Reset)
                    grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ShowArrows(grid));
            };
        }
        ShowArrows(grid);
    }

    private static void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var grid = (DataGrid)sender;
        var key = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(key)) return;
        var descending = key == GetKey(grid) ? !GetDescending(grid) : GetDescendingFirst(e.Column);
        SetKey(grid, key);
        SetDescending(grid, descending);
    }

    private static void ShowArrows(DataGrid grid)
    {
        var key = GetKey(grid);
        var direction = GetDescending(grid) ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var c in grid.Columns)
            c.SortDirection = !string.IsNullOrEmpty(key) && c.SortMemberPath == key ? direction : null;
    }
}
