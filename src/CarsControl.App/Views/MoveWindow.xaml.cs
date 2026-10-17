using System.Windows;
using CarsControl.App.ViewModels;
using Wpf.Ui.Controls;

namespace CarsControl.App.Views;

public partial class MoveWindow : FluentWindow
{
    public bool Changed { get; private set; }

    public MoveWindow(MoveViewModel vm)
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

    /// <summary>Shows the window over the main one and returns whether something moved.</summary>
    public static bool Open(MoveViewModel vm)
    {
        var window = new MoveWindow(vm) { Owner = Application.Current?.MainWindow };
        Services.Platform.ShowModal(window);
        return window.Changed;
    }
}
