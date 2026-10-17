using System.Windows;
using System.Windows.Controls;
using CarsControl.App.Services;
using CarsControl.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CarsControl.App.Views;

public partial class HomePage : Page
{
    public HomePage(HomeViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => { if (IsVisible) Services.BackgroundTasks.Run(vm.OnShownAsync(), "home refresh"); };
        Unloaded += (_, _) => vm.OnHidden();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && IsLoaded) Services.BackgroundTasks.Run(vm.OnShownAsync(), "home restored");
            else if (!IsVisible) vm.OnHidden();
        };
    }

    private static void Go(Type page) => App.Services.GetRequiredService<MainWindow>().NavigateTo(page);

    private void OnUpdatesClick(object sender, RoutedEventArgs e) => Go(typeof(RefreshPage));
    private void OnAppsClick(object sender, RoutedEventArgs e) => Go(typeof(AppsPage));
    private void OnStorageClick(object sender, RoutedEventArgs e) => Go(typeof(StoragePage));
    private void OnCleanupClick(object sender, RoutedEventArgs e) => Go(typeof(CleanupPage));
    private void OnHistoryClick(object sender, RoutedEventArgs e) => Go(typeof(HistoryPage));
    private void OnMaintenanceSettings(object sender, RoutedEventArgs e)
    {
        Go(typeof(SettingsPage));
        App.Services.GetRequiredService<PageCache>().Get<SettingsPage>().ScrollTo("Maintenance");
    }

    private void OnUpdateEverything(object sender, RoutedEventArgs e)
    {
        Go(typeof(RefreshPage));
        _ = App.Services.GetRequiredService<RefreshViewModel>().UpdateEverythingAsync();
    }
}
