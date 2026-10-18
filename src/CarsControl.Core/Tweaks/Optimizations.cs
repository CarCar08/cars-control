namespace CarsControl.Core.Tweaks;

/// <summary>
/// The Optimization page's registry switches (1.1). Same engine and rules as the privacy switches (exact undo, the helper accepts
/// only these ids), plus an evidence label on each. Deliberately missing, because measurements show no gain or a real risk:
/// network "optimizers" (Nagle, TCP autotuning), RAM cleaners, blanket service disabling, and anything touching Defender,
/// SmartScreen, the firewall, UAC, Windows Update security or memory integrity (VBS/HVCI). Power mode, hibernation and
/// animations aren't registry values and live in <see cref="CarsControl.Core.Optimize"/>.
/// </summary>
public static class Optimizations
{
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string SessionPower = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    public static IReadOnlyList<Tweak> All { get; } =
    [
        // ---------- Gaming ----------
        new()
        {
            Id = "game-mode", Group = TweakGroup.Gaming, Scope = TweakScope.User, RestoresWindowsDefault = true,
            Title = "Game Mode",
            Description = "Windows gives the game priority and holds back Windows Update installs and restart prompts while you play.",
            Evidence = Evidence.Documented,
            EvidenceNote = "On by Windows' default; some \"debloat\" tools switch it off. Microsoft describes it as helping a steadier frame rate.",
            SourceUrl = "https://support.microsoft.com/help/4028293/windows-using-game-mode-on-your-pc",
            Values = [new(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 1)],
        },
        new()
        {
            Id = "background-recording-off", FrameTestable = true, Group = TweakGroup.Gaming, Scope = TweakScope.User,
            Title = "No background game recording",
            Description = "Stops the Game Bar from constantly recording the last few minutes of play in case you want to save a clip.",
            Evidence = Evidence.Preference,
            EvidenceNote = "It records all the time, using the graphics card's video encoder and the disk. If you never save \"what just happened\" clips, that's wasted work; the frame-rate cost is usually small. Win+Alt+R still records on demand.",
            TakesEffect = "the next time you start a game",
            Values =
            [
                new(@"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                new(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1),
            ],
        },
        new()
        {
            Id = "hardware-gpu-scheduling", FrameTestable = true, Group = TweakGroup.Gaming, Scope = TweakScope.Machine,
            Title = "Hardware-accelerated GPU scheduling",
            Description = "Lets the graphics card schedule its own work instead of Windows doing it.",
            Evidence = Evidence.TestIt,
            EvidenceNote = "Independent tests find little or no frame-rate change on most PCs, gains in some games and losses in others. Measure it with Test it on this PC.",
            SourceUrl = "https://gamersnexus.net/guides/3599-windows-10-hardware-accelerated-gpu-scheduling-benchmarks",
            TakesEffect = "after a restart",
            Values = [new(GraphicsDrivers, "HwSchMode", 2, 1)],
        },
        new()
        {
            Id = "mouse-acceleration-off", Group = TweakGroup.Gaming, Scope = TweakScope.User,
            Title = "No mouse acceleration",
            Description = "Turns off \"Enhance pointer precision\": the pointer moves the same distance for the same hand movement, however fast you move.",
            Evidence = Evidence.Preference,
            EvidenceNote = "Doesn't change speed; makes aiming consistent, which is why most players turn it off. Desktop work may feel slower at first.",
            Values =
            [
                new(@"Control Panel\Mouse", "MouseSpeed", "0", "1"),
                new(@"Control Panel\Mouse", "MouseThreshold1", "0", "6"),
                new(@"Control Panel\Mouse", "MouseThreshold2", "0", "10"),
            ],
        },

        // ---------- Visual effects ----------
        new()
        {
            Id = "transparency-off", FrameTestable = true, Group = TweakGroup.Visuals, Scope = TweakScope.User,
            Title = "No transparency effects",
            Description = "Start, the taskbar and app backgrounds (C.A.R.S.' too) become solid instead of see-through.",
            Evidence = Evidence.Preference,
            EvidenceNote = "On modern graphics hardware the cost is negligible; it can help a little on old or integrated graphics and on battery.",
            Values = [new(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1)],
        },

        // ---------- Background and start-up ----------
        new()
        {
            Id = "startup-delay-off", Group = TweakGroup.Background, Scope = TweakScope.User,
            Title = "Start apps right after sign-in",
            Description = "Windows normally waits a few seconds after you sign in before starting the apps in your startup list.",
            Evidence = Evidence.TestIt,
            EvidenceNote = "Startup apps are ready sooner, but they all compete with Explorer at once. On a fast SSD it's usually a win; on a hard disk it can make sign-in feel slower.",
            TakesEffect = "at your next sign-in",
            Values = [new(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0)],
        },
        new()
        {
            Id = "fast-startup-off", Group = TweakGroup.Power, Scope = TweakScope.Machine,
            Title = "Full shutdown (no fast startup)",
            Description = "Shut down really shuts down, instead of saving the system to disk like a half-hibernate. Start-up from off takes a few seconds longer.",
            Evidence = Evidence.Documented,
            EvidenceNote = "Fast startup keeps drivers and the system session from the last boot, which Microsoft lists as a cause of update and driver problems. Restart already does a full start either way.",
            SourceUrl = "https://learn.microsoft.com/troubleshoot/windows-client/setup-upgrade-and-drivers/fast-startup-causes-system-hibernation-shutdown-fail",
            TakesEffect = "at the next shutdown",
            Values = [new(SessionPower, "HiberbootEnabled", 0, 1)],
        },

        // ---------- Network ----------
        new()
        {
            Id = "delivery-optimization-local", Group = TweakGroup.Network, Scope = TweakScope.Machine,
            Title = "Share updates only on my network",
            Description = "Windows Update still downloads normally, but this PC no longer uploads updates to strangers' PCs over the internet.",
            Evidence = Evidence.Documented,
            EvidenceNote = "Delivery Optimization can upload to other PCs on the internet, using your upload bandwidth. Sharing with PCs on your own network still works. Settings will say it's managed by your organization.",
            SourceUrl = "https://learn.microsoft.com/windows/deployment/do/waas-delivery-optimization-reference",
            Values = [new(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1)],
        },
    ];
}
