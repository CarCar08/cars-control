using System.Windows.Controls;
using CarsControl.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CarsControl.App.Views.HealthTabs;

/// <summary>Refresh › Windows Update. Has its own view model (the Refresh page's is about the update list).</summary>
public partial class WindowsUpdateTab : UserControl
{
    public WindowsUpdateTab()
    {
        InitializeComponent();
        if (App.Services?.GetService<WindowsUpdateViewModel>() is not { } vm) return;
        DataContext = vm;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) vm.Refresh();
        };
    }
}
