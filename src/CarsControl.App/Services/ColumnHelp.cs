using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CarsControl.App.Services;

internal static class ColumnHelp
{
    public static void Install()
    {
        // Not Loaded: WPF only broadcasts Loaded to elements that have an instance handler, so a class handler for it
        // never runs on template-generated headers. SizeChanged is raised on every header at its first layout.
        EventManager.RegisterClassHandler(typeof(DataGridColumnHeader), FrameworkElement.SizeChangedEvent,
            new SizeChangedEventHandler((sender, _) =>
            {
                if (sender is DataGridColumnHeader { ToolTip: null } header) Fill(header);
            }), true);
    }

    private static void Fill(DependencyObject root)
    {
        if (root is DataGridColumnHeader header && (header.ToolTip is null || header.ToolTip is string { Length: 0 }))
        {
            var name = header.Column?.Header as string ?? header.Content as string;
            if (string.IsNullOrWhiteSpace(name)) return;
            string? explanation = name switch
            {
                "Untouched" => "Time since this file or folder was last modified.",
                "Updates via" => "What keeps this app up to date: winget, the Microsoft Store, or the app's own updater.",
                "Size" => "Space this item takes on the drive, as measured or reported by its installer.",
                "Library" => "The folder where the launcher stores this game.",
                "Last played" or "Played" => "Play history reported by the launcher; some launchers don't report it.",
                "Signed" => "Whether the driver carries a digital signature Windows trusts. Unsigned drivers are rare and worth a second look.",
                "Result" => "How the operation ended, checked afterwards (for an update: whether the version really changed).",
                "Action" => "What was done: an update, uninstall, cleanup, move, privacy change…",
                "App" => "The app this row is about.",
                "When" => "When it happened, in your local time.",
                "Date" => "The date the maker gave this driver (not when it was installed).",
                "Details" => "A short summary; select the row to see the full output.",
                "Device" => "The hardware this driver runs, as Device Manager names it.",
                "Maker" => "The company that published this driver.",
                "Type" => "What kind of item this is: the device class for a driver; a file, folder or registry value for a leftover.",
                "Version" => "The installed version number.",
                "Via" => "Which tool did the work: winget, Windows Update, the admin helper, or C.A.R.S. itself.",
                _ => null,
            };
            var sort = header.Column?.CanUserSort == true ? $"Click to sort by {name}; click again to reverse the order." : null;
            if (explanation is null && sort is null) return;
            header.ToolTip = string.Join(" ", new[] { explanation, sort }.Where(s => s is not null));
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Fill(VisualTreeHelper.GetChild(root, i));
    }
}
