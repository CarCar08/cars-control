using System.Windows.Controls;
using CarsControl.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CarsControl.App.Views.StorageTabs;

public partial class JunkTab : UserControl
{
    public JunkTab()
    {
        InitializeComponent();
        if (App.Services?.GetService<JunkViewModel>() is not { } vm) return;
        DataContext = vm;
    }
}
