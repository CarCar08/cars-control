using Microsoft.Toolkit.Uwp.Notifications;

namespace CarsControl.App.Services;

/// <summary>Windows toasts for an unpackaged app (the compat library registers the AUMID and activator
/// under HKCU). Clicking "Review" opens the Refresh page; "Approve" runs the waiting admin items.</summary>
public static class Notifier
{
    /// <summary>A toast (or one of its buttons) asked for a page.</summary>
    public static event Action<string>? Activated;

    /// <summary>A toast button asked for an action ("approve").</summary>
    public static event Action<string>? ActionInvoked;

    public static void Initialize()
    {
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            var args = ToastArguments.Parse(e.Argument);
            if (args.TryGetValue("action", out var action))
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ActionInvoked?.Invoke(action));
                return;
            }
            var page = args.TryGetValue("page", out var p) ? p : "refresh";
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Activated?.Invoke(page));
        };
    }

    public static void UpdatesReady(int count, string detail, int adminCount)
    {
        Show(new ToastContentBuilder()
            .AddArgument("page", "refresh")
            .AddText(count == 1 ? "1 update ready" : $"{count} updates ready")
            .AddText(detail)
            .AddAttributionText(adminCount > 0 ? $"{adminCount} need admin — you'll be asked once" : "No admin needed")
            .AddButton(new ToastButton().SetContent("Review").AddArgument("page", "refresh"))
            .AddButton(new ToastButtonDismiss("Later")));
    }

    /// <summary>Shown when you're back at the PC: one click, one prompt.</summary>
    public static void ApprovalsWaiting(int count, string detail)
    {
        Show(new ToastContentBuilder()
            .AddArgument("page", "home")
            .AddText(count == 1 ? "1 admin item is waiting" : $"{count} admin items are waiting")
            .AddText($"{detail} from scheduled maintenance. Approve once to run them.")
            .AddButton(new ToastButton().SetContent("Approve").AddArgument("action", "approve"))
            .AddButton(new ToastButtonDismiss("Later")));
    }

    public static void AppUpdateReady(string version)
    {
        Show(new ToastContentBuilder()
            .AddArgument("page", "settings")
            .AddText($"C.A.R.S. Control {version} is ready")
            .AddText("Downloaded and checked. Your settings and data are kept.")
            .AddButton(new ToastButton().SetContent("Update now").AddArgument("action", "selfupdate"))
            .AddButton(new ToastButtonDismiss("Later")));
    }

    /// <summary>Restart when I'm away: five minutes of warning, with a Cancel button.</summary>
    public static void RestartCountdown(int minutes)
    {
        Show(new ToastContentBuilder()
            .AddArgument("page", "health")
            .AddText($"Restarting in {minutes} minutes")
            .AddText("Windows has an update waiting and you've been away. Move the mouse or press Cancel to stop it.")
            .AddButton(new ToastButton().SetContent("Cancel the restart").AddArgument("action", "restart-cancel"))
            .AddAudio(new ToastAudio { Silent = true }));
    }

    public static void Info(string title, string text)
    {
        Show(new ToastContentBuilder().AddArgument("page", "health").AddText(title).AddText(text));
    }

    public static void MaintenanceDone(string origin, string summary)
    {
        Show(new ToastContentBuilder()
            .AddArgument("page", "history")
            .AddText($"{origin} finished")
            .AddText(summary)
            .AddButton(new ToastButton().SetContent("Show details").AddArgument("page", "history"))
            .AddButton(new ToastButtonDismiss("OK")));
    }

    private static void Show(ToastContentBuilder toast)
    {
        try
        {
            toast.Show();
        }
        catch
        {
            // Notifications disabled / Focus Assist: Home and the tray tooltip still show it.
        }
    }
}
