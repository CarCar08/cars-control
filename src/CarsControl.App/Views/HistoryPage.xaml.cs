using System.Windows.Controls;
using CarsControl.App.ViewModels;

namespace CarsControl.App.Views;

public partial class HistoryPage : Page
{
    public HistoryPage(HistoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => vm.Reload();
    }
}
