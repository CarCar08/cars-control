using System.Windows.Controls;
using CarsControl.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CarsControl.App.Views.StorageTabs;

public partial class DuplicatesTab : UserControl
{
    public DuplicatesTab()
    {
        InitializeComponent();
        if (App.Services?.GetService<DuplicatesViewModel>() is not { } vm) return;
        DataContext = vm;
    }
}
