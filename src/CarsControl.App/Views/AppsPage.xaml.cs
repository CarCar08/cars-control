using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CarsControl.App.ViewModels;

namespace CarsControl.App.Views;

public partial class AppsPage : Page
{
    private readonly AppsViewModel _vm;

    public AppsPage(AppsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    /// <summary>0 = Installed, 1 = Get apps, 2 = Startup, 3 = Right-click menu.</summary>
    public void ShowTab(int index) => Tabs.SelectedIndex = Math.Clamp(index, 0, Tabs.Items.Count - 1);

    /// <summary>Right-click selects the row under the cursor first. Before, the context menu acted on
    /// whichever row was selected earlier (or none), which is why its items seemed to do nothing.</summary>
    private void OnPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource) is { } row)
        {
            row.IsSelected = true;
            Grid.SelectedItem = row.Item;
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource) is { Item: AppItemViewModel item }) _vm.ShowDetailsCommand.Execute(item);
    }

    private static DataGridRow? RowAt(object source)
    {
        var d = source as DependencyObject;
        while (d is not null and not DataGridRow) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as DataGridRow;
    }
}
