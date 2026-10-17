using CarsControl.App.ViewModels;
using Wpf.Ui.Controls;

namespace CarsControl.App.Views;

public partial class UninstallWindow : FluentWindow
{
    public bool Changed { get; private set; }

    public UninstallWindow(UninstallViewModel vm)
    {
        InitializeComponent();
        Services.AppearanceService.Track(this);
        DataContext = vm;
        vm.CloseRequested += changed =>
        {
            Changed = changed;
            Close();
        };
    }
}
