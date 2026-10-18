using System.Windows;
using CarsControl.App.ViewModels;
using Wpf.Ui.Controls;

namespace CarsControl.App.Views;

public partial class FrameTestWindow : FluentWindow
{
    public FrameTestWindow(FrameTestViewModel vm)
    {
        InitializeComponent();
        Services.AppearanceService.Track(this);
        DataContext = vm;
        vm.CloseRequested += Close;
    }

    public static void Open(FrameTestViewModel vm) =>
        Services.Platform.ShowModal(new FrameTestWindow(vm) { Owner = Application.Current?.MainWindow });
}
