using System.Windows.Controls;
using CarsControl.App.ViewModels;

namespace CarsControl.App.Views;

public partial class OptimizePage : Page
{
    public OptimizePage(OptimizeViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // Settings can change outside C.A.R.S. (Windows Settings, other tools): read them live each time the page is shown.
        Loaded += (_, _) => vm.Refresh();
    }
}
