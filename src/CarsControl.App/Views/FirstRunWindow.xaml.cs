using CarsControl.App.Services;
using CarsControl.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace CarsControl.App.Views;

/// <summary>The first-time setup (see <see cref="FirstRunViewModel"/>). Opens over the main window.</summary>
public partial class FirstRunWindow : FluentWindow
{
    private static FirstRunWindow? _open;

    private FirstRunWindow(FirstRunViewModel vm)
    {
        InitializeComponent();
        AppearanceService.Track(this);
        DataContext = vm;
        vm.CloseRequested += Close;
        vm.OpenSettingsSection += section => Navigator.Go("settings", section);
        vm.OpenPage += Navigator.Go;
        if (System.Windows.Application.Current.MainWindow is { IsVisible: true } owner) Owner = owner;
        if (App.IsDevInstance) { ShowActivated = false; SourceInitialized += (_, _) => Services.DevFocusGuard.NoActivate(this); }
        Closed += (_, _) => _open = null;
    }

    public static void Open()
    {
        if (_open is { } existing)
        {
            if (!App.IsDevInstance) existing.Activate();
            return;
        }
        var vm = new FirstRunViewModel(App.Services.GetRequiredService<AppSettings>(), App.Services.GetRequiredService<PrivacyViewModel>());
        _open = new FirstRunWindow(vm);
        _open.Show();
    }
}
