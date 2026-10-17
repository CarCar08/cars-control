using System.Windows.Controls;
using CarsControl.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CarsControl.App.Views.StorageTabs;

public partial class MoveFoldersTab : UserControl
{
    public MoveFoldersTab()
    {
        InitializeComponent();
        if (App.Services?.GetService<MoveFoldersViewModel>() is not { } vm) return;
        DataContext = vm;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) vm.Refresh();
        };
    }
}
