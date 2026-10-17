using System.Windows.Controls;
using CarsControl.App.ViewModels;

namespace CarsControl.App.Views;

public partial class PrivacyPage : Page
{
    public PrivacyPage(PrivacyViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // Read live every time: Settings, group policy or another tool may have changed things meanwhile.
        Loaded += (_, _) => vm.Refresh();
    }
}
