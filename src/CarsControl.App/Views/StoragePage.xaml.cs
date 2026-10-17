using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CarsControl.App.ViewModels;

namespace CarsControl.App.Views;

public partial class StoragePage : Page
{
    private readonly StorageViewModel _vm;

    public StoragePage(StorageViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Map.NodeActivated += node => _vm.NavigateCommand.Execute(node);
        Loaded += (_, _) =>
        {
            _vm.RefreshDrives();
            if (!_vm.ReportsLoaded) _ = _vm.LoadReportsAsync();
        };
    }

    private bool ExploreTabActive => Tabs.SelectedIndex == 0;

    /// <summary>0 Explore, 1 Big &amp; old files, 2 Duplicates, 3 Junk, 4 Games, 5 Move folders, 6 Old Windows (the command palette opens them).</summary>
    public void ShowTab(int index) => Tabs.SelectedIndex = Math.Clamp(index, 0, Tabs.Items.Count - 1);

    /// <summary>Mouse side buttons go back and forward on Explore, like File Explorer.</summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ExploreTabActive) return;
        if (e.ChangedButton == MouseButton.XButton1 && _vm.BackCommand.CanExecute(null))
        {
            _vm.BackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.XButton2 && _vm.ForwardCommand.CanExecute(null))
        {
            _vm.ForwardCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Alt+Left / Alt+Right / Alt+Up, Backspace (outside text boxes) and Enter on a folder, as in File Explorer.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!ExploreTabActive) return;
        if (e.Key == Key.Enter && FolderGrid.IsKeyboardFocusWithin && FolderGrid.SelectedItem is FolderRow selected)
        {
            _vm.OpenFolderRowCommand.Execute(selected);
            e.Handled = true;
            return;
        }
        var alt = Keyboard.Modifiers == ModifierKeys.Alt;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        ICommand? command = (alt, key) switch
        {
            (true, Key.Left) => _vm.BackCommand,
            (true, Key.Right) => _vm.ForwardCommand,
            (true, Key.Up) => _vm.UpCommand,
            (false, Key.Back) when Keyboard.FocusedElement is not TextBoxBase => _vm.BackCommand,
            _ => null,
        };
        if (command is null) return;
        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
    }

    private void OnFolderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e) is { Item: FolderRow row }) _vm.OpenFolderRowCommand.Execute(row);
    }

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e) is { Item: BigFileRow row }) _vm.ExploreCommand.Execute(row.File.Path);
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SetSelectedFiles(FileGrid.SelectedItems.OfType<BigFileRow>());

    /// <summary>Right-click acts on the row under the mouse (kept when it's part of a multi-selection).</summary>
    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || RowUnder(e) is not { } row || row.IsSelected) return;
        // Single-selection grids (Games) throw if SelectedItems is changed directly.
        if (grid.SelectionMode == DataGridSelectionMode.Single) grid.SelectedItem = row.Item;
        else { grid.SelectedItems.Clear(); row.IsSelected = true; }
        row.Focus();
    }

    private static DataGridRow? RowUnder(MouseEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is DataGridRow r) return r;
            if (d is DataGridColumnHeader) return null;
        }
        return null;
    }
}
