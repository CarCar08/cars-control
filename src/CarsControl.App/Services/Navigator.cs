using CarsControl.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CarsControl.App.Services;

/// <summary>Lets view models open a page by name ("history", "refresh"…) without knowing about the
/// window, so they stay free of WPF types (a future macOS UI only needs its own Navigator).</summary>
public static class Navigator
{
    /// <summary>Opens a page, and optionally a tab or section on it: ("storage", "files"),
    /// ("settings", "Maintenance"). Also brings the window back from the tray.</summary>
    public static void Go(string page, string? section = null)
    {
        if (App.Services?.GetService<MainWindow>() is not { } window || MainWindow.PageFromName(page) is not { } type) return;
        if (!window.IsVisible) window.ShowFromTray();
        window.NavigateTo(type);
        if (section is null) return;
        switch (page)
        {
            case "storage":
                App.Services.GetRequiredService<PageCache>().Get<StoragePage>().ShowTab(section switch
                {
                    "files" => 1,
                    "duplicates" => 2,
                    "junk" => 3,
                    "games" => 4,
                    "move" => 5,
                    "oldwindows" => 6,
                    _ => 0,
                });
                break;
            case "backup":
                App.Services.GetRequiredService<PageCache>().Get<BackupPage>().ShowTab(section switch { "setup" => 1, "pcs" => 2, _ => 0 });
                break;
            case "apps":
                App.Services.GetRequiredService<PageCache>().Get<AppsPage>().ShowTab(section switch { "get" => 1, "startup" => 2, "menu" => 3, _ => 0 });
                break;
            case "refresh":
                App.Services.GetRequiredService<PageCache>().Get<RefreshPage>().ShowTab(section == "windowsupdate" ? 1 : 0);
                break;
            case "health":
                App.Services.GetRequiredService<PageCache>().Get<HealthPage>().ShowTab(section switch
                {
                    "sensors" => ViewModels.HealthTab.Sensors,
                    "drives" => ViewModels.HealthTab.Drives,
                    "crashes" => ViewModels.HealthTab.Crashes,
                    "sleep" => ViewModels.HealthTab.SleepBoot,
                    "drivers" => ViewModels.HealthTab.Drivers,
                    "activity" => ViewModels.HealthTab.Activity,
                    "network" => ViewModels.HealthTab.Network,
                    _ => ViewModels.HealthTab.Overview,
                });
                break;
            case "settings":
                App.Services.GetRequiredService<PageCache>().Get<SettingsPage>().ScrollTo(section);
                break;
        }
    }
}
