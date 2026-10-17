using System.Windows.Controls;
using CarsControl.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CarsControl.App.Views.AppsTabs;

public partial class GetAppsTab : UserControl
{
    public GetAppsTab()
    {
        InitializeComponent();
        if (App.Services?.GetService<GetAppsViewModel>() is not { } vm) return;
        DataContext = vm;
    }
}
