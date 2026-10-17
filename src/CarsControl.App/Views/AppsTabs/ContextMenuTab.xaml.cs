using System.Windows.Controls;
using CarsControl.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CarsControl.App.Views.AppsTabs;

public partial class ContextMenuTab : UserControl
{
    public ContextMenuTab()
    {
        InitializeComponent();
        if (App.Services?.GetService<ContextMenuViewModel>() is not { } vm) return;
        DataContext = vm;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) vm.Refresh();
        };
    }
}
