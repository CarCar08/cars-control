using CarsControl.Core.Models;

namespace CarsControl.Core.Inventory;

/// <summary>Decides which mechanism keeps each app current. Order matters: launchers are checked
/// before winget correlation because winget re-running their installers breaks them (Epic, 2026-10-07:
/// the MSI deleted EpicGamesLauncher.exe while every layer reported success).</summary>
public static class ChannelClassifier
{
    private static readonly string[] LauncherNames =
    [
        "Steam", "Epic Games Launcher", "EA app", "EA Desktop", "Ubisoft Connect", "Rockstar Games Launcher",
        "Riot Client", "Battle.net", "GOG GALAXY", "Xbox",
    ];

    private static readonly string[] LauncherWingetIds =
    [
        "Valve.Steam", "EpicGames.EpicGamesLauncher", "ElectronicArts.EADesktop", "Ubisoft.Connect",
        "RockstarGames.Launcher", "Blizzard.BattleNet", "GOG.Galaxy",
    ];

    // Apps that ship their own updater and are not (reliably) in winget.
    private static readonly string[] SelfUpdatingNames =
    [
        "Discord", "Razer Synapse", "Razer Chroma", "Razer Cortex", "NVIDIA App", "Riot Vanguard",
        "Mozilla Maintenance Service", "Wallpaper Engine", "OpenIV", "Denuvo Anti-Cheat", "THX Spatial Audio",
    ];

    // Packages winget can see but should not drive, because a better official channel exists.
    private static readonly Dictionary<string, UpdateChannel> WingetOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.WSL"] = UpdateChannel.Wsl,          // winget's MSI path fails here (0x80073CF9); use `wsl --update`
        ["Microsoft.GameInput"] = UpdateChannel.System, // serviced by Windows Update / games, MSI 1603 via winget
        ["Microsoft.Office"] = UpdateChannel.Office,
    };

    private static readonly string[] GamePathMarkers =
    [
        @"\steamapps\common\", @"\SteamLibrary\", @"\Epic Games\", @"\Riot Games\", @"\Rockstar Games\",
        @"\EA Games\", @"\Ubisoft Game Launcher\games\", @"\XboxGames\",
    ];

    public static UpdateChannel Classify(AppEntry a)
    {
        var name = a.Name;

        if (a.WingetId is { } wid && LauncherWingetIds.Contains(wid, StringComparer.OrdinalIgnoreCase)) return UpdateChannel.Launcher;
        if (LauncherNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase))) return UpdateChannel.Launcher;

        if (a.WingetId is { } id && WingetOverrides.TryGetValue(id, out var forced)) return forced;
        if (name.StartsWith("Windows Subsystem for Linux", StringComparison.OrdinalIgnoreCase)) return UpdateChannel.Wsl;
        if (IsOffice(a)) return UpdateChannel.Office;

        if (IsGame(a)) return UpdateChannel.Game;

        if (a.WingetId is not null) return UpdateChannel.Winget;

        if (SelfUpdatingNames.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase))) return UpdateChannel.SelfUpdating;

        if (IsDriver(a)) return UpdateChannel.Driver;

        if (a.Source == AppSource.Msix) return a.Channel == UpdateChannel.Store ? UpdateChannel.Store : UpdateChannel.None;

        if (a.Publisher?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) == true &&
            (name.Contains("Update Health", StringComparison.OrdinalIgnoreCase) || name.Contains("Compatibility", StringComparison.OrdinalIgnoreCase)))
            return UpdateChannel.System;

        return UpdateChannel.None;
    }

    private static bool IsOffice(AppEntry a) =>
        a.Arp is not null &&
        (a.Name.StartsWith("Microsoft 365", StringComparison.OrdinalIgnoreCase) ||
         a.Name.StartsWith("Microsoft Office", StringComparison.OrdinalIgnoreCase)) &&
        (a.UninstallString?.Contains("OfficeClickToRun", StringComparison.OrdinalIgnoreCase) ?? false);

    internal static bool IsGame(AppEntry a)
    {
        if (a.Arp?.KeyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (a.UninstallString?.Contains("steam://uninstall", StringComparison.OrdinalIgnoreCase) == true) return true;
        var loc = a.InstallLocation;
        if (loc is null) return false;
        // The Epic/EA/Rockstar launchers themselves live under these folders too; they were caught above.
        return GamePathMarkers.Any(m => (loc + "\\").Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDriver(AppEntry a)
    {
        var pub = a.Publisher ?? "";
        var name = a.Name;
        if (name.Contains("Driver", StringComparison.OrdinalIgnoreCase)) return true;
        if (pub.StartsWith("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) || name.StartsWith("AMD ", StringComparison.OrdinalIgnoreCase)) return true;
        if (pub.StartsWith("Realtek", StringComparison.OrdinalIgnoreCase) || pub.StartsWith("Intel", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Human label used in the Apps list and Refresh page.</summary>
    public static string Describe(UpdateChannel c) => c switch
    {
        UpdateChannel.Winget => "winget",
        UpdateChannel.Store => "Store",
        UpdateChannel.SelfUpdating => "Updates itself",
        UpdateChannel.Launcher => "Self (launcher)",
        UpdateChannel.Game => "Its launcher",
        UpdateChannel.Office => "Office",
        UpdateChannel.Wsl => "wsl --update",
        UpdateChannel.Driver => "Vendor tool",
        UpdateChannel.System => "Windows",
        _ => "—",
    };
}
