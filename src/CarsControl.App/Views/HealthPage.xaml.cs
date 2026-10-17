using System.Windows.Controls;
using CarsControl.App.ViewModels;

namespace CarsControl.App.Views;

public partial class HealthPage : Page
{
    private readonly HealthViewModel _vm;

    public HealthPage(HealthViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        // Polling (sensors, activity) runs only while this page is on screen.
        Loaded += (_, _) => { if (IsVisible) vm.PageShown(); };
        Unloaded += (_, _) => vm.PageHidden();
        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded) vm.PageShown(); else vm.PageHidden(); };
    }

    /// <summary>Opens a tab by name (palette, Home links): "sensors", "crashes", "sleep"…</summary>
    public void ShowTab(HealthTab tab) => _vm.SelectedTab = tab;
}
