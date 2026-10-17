namespace CarsControl.Core.Startup;

/// <summary>What a few well-known start-up items do, and what switching them off costs. Shown on the row and in the
/// confirmation, so "Proton VPN" doesn't get switched off by someone who thinks it's clutter.</summary>
public static class StartupNotes
{
    private static readonly (string Match, string Note)[] Known =
    [
        ("CarsControl", "This app. Settings › Background work has the same switch."),
        ("Proton VPN", "Starts Proton VPN after you sign in. This PC's exit-node guard and the always-on VPN rely on it, so a restart would leave you without the VPN until you open it by hand."),
        ("ProtonVPN", "Starts Proton VPN after you sign in. This PC's exit-node guard and the always-on VPN rely on it."),
        ("Tailscale", "Starts Tailscale's tray icon. The connection itself is a service, which keeps running either way."),
        ("SecurityHealth", "The Windows Security icon in the tray. Windows Defender keeps protecting the PC without it."),
        ("MicrosoftEdgeAutoLaunch", "Lets Edge start in the background so it opens faster. Safe to switch off."),
        ("WingetUI", "UniGetUI, the update manager. It's only needed if you still use it."),
        ("Everything", "The instant file search's background indexer. Searches still work without it, but start slower."),
        ("RtkAudUService", "Realtek audio's helper (the audio panel and jack detection). Sound works without it."),
        ("Riot Vanguard", "Riot's anti-cheat. Valorant and League won't start without it."),
        ("RiotClient", "Riot's launcher. It opens when you start a Riot game anyway."),
        ("Discord", "Discord opening at sign-in. It starts when you open it yourself."),
        ("Steam", "Steam opening at sign-in. Games can't start until you open it."),
        ("OneDrive", "Syncs your OneDrive files. Nothing syncs while it's off."),
        ("SignalRgb", "The RGB lighting controller. Your lights go back to their hardware default without it."),
        ("Cryptomator", "Unlocks your encrypted vaults when you open it. Vaults stay locked until you start it."),
    ];

    public static string? For(string name) =>
        Known.FirstOrDefault(k => name.StartsWith(k.Match, StringComparison.OrdinalIgnoreCase)).Note;
}
