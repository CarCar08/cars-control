using System.Windows.Controls;
using CarsControl.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CarsControl.App.Views.AppsTabs;

public partial class StartupTab : UserControl
{
    public StartupTab()
    {
        InitializeComponent();
        if (App.Services?.GetService<StartupViewModel>() is not { } vm) return;
        DataContext = vm;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) _ = vm.RefreshAsync();
        };
    }
}
