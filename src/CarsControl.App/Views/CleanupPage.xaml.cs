using System.Windows.Controls;
using CarsControl.App.ViewModels;

namespace CarsControl.App.Views;

public partial class CleanupPage : Page
{
    public CleanupPage(CleanupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => _ = vm.EnsureEstimatesAsync();
    }
}
