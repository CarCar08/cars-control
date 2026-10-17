using System.Windows.Controls;
using CarsControl.App.ViewModels;

namespace CarsControl.App.Views;

public partial class RefreshPage : Page
{
    public RefreshPage(RefreshViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => vm.EnsureFresh();
    }

    /// <summary>0 = Updates, 1 = Windows Update.</summary>
    public void ShowTab(int index) => Tabs.SelectedIndex = Math.Clamp(index, 0, Tabs.Items.Count - 1);
}
