using CarsControl.Core.Models;
using CarsControl.Core.Storage;
using CarsControl.Core.Uninstall;

namespace CarsControl.Tests;

public class FileKindTests
{
    [Theory]
    [InlineData(@"A:\Videos\movie.mkv", FileKind.Video)]
    [InlineData(@"D:\SteamLibrary\steamapps\common\GTA\GTA5.exe", FileKind.GameData)] // the game, not an installer
    [InlineData(@"C:\Users\alex\Downloads\LocalSend.exe", FileKind.Installer)]
    [InlineData(@"C:\Temp\VSCodeUserSetup-x64.exe", FileKind.Installer)]
    [InlineData(@"C:\Program Files\NVIDIA\app.exe", FileKind.Other)]                    // a program, not an installer
    [InlineData(@"C:\Users\alex\Desktop\big.msi", FileKind.Installer)]
    [InlineData(@"C:\Users\alex\AppData\Local\Google\OptGuideOnDeviceModel\weights.bin", FileKind.AiModel)]
    [InlineData(@"C:\firmware.bin", FileKind.Other)]
    [InlineData(@"D:\Windows\LiveKernelReports\WATCHDOG-20260518-1201.dmp", FileKind.DumpOrLog)]
    [InlineData(@"C:\Games\MarvelRivals\pakchunk0-Windows.ucas", FileKind.GameData)]
    public void Groups_files_by_type(string path, FileKind kind) => Assert.Equal(kind, FileKinds.Of(path));
}

public class FileOwnershipRound3Tests
{
    [Fact]
    public void Page_and_hibernation_files_are_windows_files() // D:\pagefile.sys was offered for Recycle in round 2
    {
        var o = new FileOwnership([], []).Classify(@"D:\pagefile.sys");
        Assert.Equal(OwnerKind.System, o.Kind);
        Assert.False(o.CanRecycle);
        Assert.Equal(OwnerKind.Free, new FileOwnership([], []).Classify(@"D:\Backups\pagefile.sys").Kind); // only at a drive root
    }

    [Fact]
    public void Cloud_synced_files_can_be_freed_up_and_warn_before_recycling()
    {
        var o = new FileOwnership([], [], [new SyncRoot(@"A:\Windows (Backup)\iCloudDrive", "iCloud Drive")])
            .Classify(@"A:\Windows (Backup)\iCloudDrive\Videos\Movies\big.mkv");
        Assert.Equal(OwnerKind.Synced, o.Kind);
        Assert.True(o.CanFreeUp);
        Assert.True(o.CanRecycle);
        Assert.True(o.IsWarning);
        Assert.Equal("iCloud Drive", o.Provider);
        Assert.Contains("deletes it from iCloud Drive too", o.Text);
    }

    [Fact]
    public void A_folder_next_to_a_sync_root_is_not_synced() // A:\Windows (Backup)\iCloud\iCloudDrive is an old, unsynced copy
    {
        var o = new FileOwnership([], [], [new SyncRoot(@"A:\Windows (Backup)\iCloudDrive", "iCloud Drive")])
            .Classify(@"A:\Windows (Backup)\iCloud\iCloudDrive\Videos\big.mkv");
        Assert.Equal(OwnerKind.Free, o.Kind);
    }

    [Fact]
    public void Cryptomator_vault_files_are_never_recycled()
    {
        var o = new FileOwnership([], [], [new SyncRoot(@"A:\Proton Drive\My files", "Proton Drive")])
            .Classify(@"A:\Proton Drive\My files\Proton Vault\d\AB\CDEF\x.c9r");
        Assert.Equal(OwnerKind.System, o.Kind);
        Assert.False(o.CanRecycle);
        Assert.False(o.CanFreeUp);
    }

    [Theory]
    [InlineData("iCloudDrive!S-1-5-21-1!Personal", "iCloud Drive")]
    [InlineData("ProtonDrive!S-1-5-21-1!abc==!7", "Proton Drive")]
    [InlineData("Dropbox!S-1-5-21-1!x", "Dropbox")]
    public void Names_cloud_providers(string id, string name) => Assert.Equal(name, CloudSync.ProviderName(id));
}

public class RemovedAppTests
{
    [Fact]
    public void Rebuilds_a_steam_game_from_an_old_history_row() // How to Fish, removed before round 3
    {
        var app = RemovedApp.FromHistory("arp:LocalMachine64:Steam App 4001890", "How to Fish", null);
        Assert.Equal("How to Fish", app.Name);
        Assert.Equal(new ArpLocation(ArpHive.LocalMachine64, "Steam App 4001890"), app.Arp);
        Assert.Equal(InstallScope.Machine, app.Scope);
        Assert.Equal("4001890", LeftoverScanner.SteamAppId(app));
    }

    [Fact]
    public void Round_trips_the_full_entry_saved_at_uninstall()
    {
        var original = new AppEntry
        {
            Key = "arp:CurrentUser:LocalSend", Name = "LocalSend", Publisher = "Tien Do Nam", Source = AppSource.Arp,
            Arp = new ArpLocation(ArpHive.CurrentUser, "LocalSend"), InstallLocation = @"C:\Users\alex\AppData\Local\Programs\LocalSend",
        };
        var back = RemovedApp.FromHistory(original.Key, "ignored", RemovedApp.ToData(original));
        Assert.Equal("LocalSend", back.Name);
        Assert.Equal("Tien Do Nam", back.Publisher);
        Assert.Equal(original.InstallLocation, back.InstallLocation);
        Assert.Equal(original.Arp, back.Arp);
    }

    [Fact]
    public void Ignores_payloads_that_are_not_an_app() // leftover rows store quarantine ids in Data
    {
        var app = RemovedApp.FromHistory("msix:aimgr_0.20.62.0_x64__8wekyb3d8bbwe", "Local AI Manager", """{"user":"x","machine":null}""");
        Assert.Equal(AppSource.Msix, app.Source);
        Assert.Equal("aimgr_0.20.62.0_x64__8wekyb3d8bbwe", app.MsixFullName);
    }

    [Theory]
    [InlineData("Steam App 271590", "271590")]
    [InlineData("Steam App 12; calc", null)]
    [InlineData("LocalSend", null)]
    public void Only_numeric_steam_ids(string keyName, string? id) =>
        Assert.Equal(id, LeftoverScanner.SteamAppId(new AppEntry { Key = "k", Name = "n", Arp = new ArpLocation(ArpHive.LocalMachine64, keyName) }));
}
