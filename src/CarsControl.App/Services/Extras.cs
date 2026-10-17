using CarsControl.Core.Health;

namespace CarsControl.App.Services;

/// <summary>
/// Which of the apps some features are built around are on this PC, read once at start-up from the inventory cache, and the
/// texts that only mention them when they are. Views use these through x:Static.
/// </summary>
public static class Extras
{
    public static IReadOnlyList<string> AppNames { get; private set; } = [];
    public static bool Salad { get; private set; }

    public static void Init(IEnumerable<string> appNames)
    {
        AppNames = appNames.ToList();
        // Screenshot (demo) mode only trusts the demo app list: this PC's folders and network adapters stay out of it.
        Salad = App.IsDemo ? PersonalExtras.SaladInstalled(AppNames, _ => false) : PersonalExtras.SaladInstalled(AppNames);
    }

    public static bool ProtonVpn(IReadOnlyList<NetAdapter> adapters) => PersonalExtras.ProtonVpnInstalled(App.IsDemo ? [] : adapters, AppNames);

    public static string GamingIntro => Salad
        ? "While you play, C.A.R.S. pauses its own background work (update checks, maintenance). Salad, notifications and your power plan are left alone."
        : "While you play, C.A.R.S. pauses its own background work (update checks, maintenance). Notifications and your power plan are left alone.";

    public static string GamingToggleTip => "Manually switch Gaming mode on or off. Background maintenance is postponed while it is on" + (Salad ? "; Salad is never paused." : ".");

    public static string GamingAutomaticTip => "Detect a running game and postpone background maintenance until it ends." + (Salad ? " Salad is always protected." : "");

    public static string ActivityIntro => "Updates every 3 seconds while this tab is open." + (Salad
        ? " Salad's processes are labelled and never offered to close: they're how this PC earns."
        : " Windows' own processes and security software are labelled and never offered to close.");
}
