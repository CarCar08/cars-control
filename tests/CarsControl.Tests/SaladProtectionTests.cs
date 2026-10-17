using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Inventory;
using CarsControl.Core.Models;
using CarsControl.Core.Updates;
using CarsControl.Core.Uninstall;
using CarsControl.Core.Util;

namespace CarsControl.Tests;

public class SaladProtectionTests
{
    [Theory]
    [InlineData("<Task><Actions><Exec><Command>C:\\Salad\\Salad.exe</Command></Exec></Actions></Task>", false)]
    [InlineData("<Task><Actions><Exec><Command>C:\\&#83;alad\\app.exe</Command></Exec></Actions></Task>", false)]
    [InlineData("<Task><Actions><Exec><Command>C:\\Other\\app.exe</Command></Exec></Actions></Task>", true)]
    [InlineData("<Task><Actions><Exec><Command>powershell.exe</Command><Arguments>-File C:\\Salad\\start.ps1</Arguments></Exec></Actions></Task>", false)]
    [InlineData("<Task><Actions><ComHandler><ClassId>{01575CFE-9A55-4003-A5E1-F38D1EBDCBE1}</ClassId></ComHandler></Actions></Task>", true)]
    [InlineData("<Task />", false)]
    [InlineData("bad xml", false)]
    public void Renamed_tasks_are_checked_from_their_actual_commands(string definition, bool allowed) =>
        Assert.Equal(allowed, CarsControl.Core.Startup.TaskRules.MayChangeDefinition(definition));

    [Fact]
    public async Task Startup_and_menu_guards_include_renamed_entries_with_salad_commands()
    {
        var item = new CarsControl.Core.Startup.StartupItem("run:HKCU:test", CarsControl.Core.Startup.StartupKind.Run,
            CarsControl.Core.Startup.StartupScope.You, "Renamed earnings app", @"C:\Salad\Salad.exe --tray", null, null, true, "test");
        Assert.True(CarsControl.Core.Startup.StartupWriter.IsPermanentlyProtected(item));
        Assert.False(CarsControl.Core.Startup.StartupWriter.TrySetYours(item, false));
        var menu = new CarsControl.Core.ContextMenu.MenuEntry("test", CarsControl.Core.ContextMenu.MenuPlace.Files,
            CarsControl.Core.ContextMenu.MenuEntryKind.Verb, "Renamed entry", @"C:\Salad\Salad.exe", null, false, null, false, null);
        Assert.True(menu.Locked);
        Assert.False(CarsControl.Core.ContextMenu.ContextMenuEntries.SetHidden(menu, true));
        var helper = new HelperOperations(new HelperLog(), null);
        Assert.Equal(OpStatus.Rejected, (await helper.ExecuteAsync(new StartupEntryOp("run", "Salad", false), true, _ => { }, CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("Salad")]
    [InlineData("SaladBowl")]
    [InlineData("salad 1.0")]
    public async Task Earnings_app_cannot_be_unprotected_uninstalled_or_updated(string name)
    {
        var app = new AppEntry { Key = "test", Name = name, Source = AppSource.Arp };
        Assert.True(new ProtectedApps([]).IsProtected(app));
        Assert.Equal(UninstallMethod.None, UninstallPlanner.Plan(app).Method);
        var uninstall = await UninstallExecutor.ExecuteAsync(app, new UninstallPlan(UninstallMethod.Msi, false, "test"), null, _ => { }, false, CancellationToken.None);
        Assert.False(uninstall.Ok);
        var update = await new UpdateExecutor(new NoHelper()).RunAsync(new UpdateCandidate { Key = "test", Title = name, Source = UpdateSource.Winget }, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.Rejected, update.Status);
    }

    [Fact]
    public async Task Helper_rejects_salad_ids_before_looking_for_any_executable()
    {
        var helper = new HelperOperations(new HelperLog(), null);
        foreach (var op in new HelperOp[] { new WingetUpgradeOp("SaladTechnologies.Salad"), new WingetInstallOp("SaladTechnologies.Salad"), new WingetUninstallOp("SaladTechnologies.Salad") })
            Assert.Equal(OpStatus.Rejected, (await helper.ExecuteAsync(op, true, _ => { }, CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("Salad", @"C:\Program Files\Salad\Salad.exe")]
    [InlineData("xmrig", @"C:\miners\xmrig.exe")]
    [InlineData("vmmemWSL", @"C:\Windows\vmmemWSL.exe")]
    public async Task Close_apps_never_opens_process_handles_for_earnings_or_wsl(string name, string path) =>
        Assert.False(await RunningApps.CloseGracefullyAsync([new RunningProcess(-1, name, path)], TimeSpan.Zero));

    private sealed class NoHelper : IHelperChannel
    {
        public bool IsConnected => false;
        public Task<OpResult> SendAsync(HelperOp op, Action<string>? onLine = null, bool dryRun = false, CancellationToken ct = default) => throw new Xunit.Sdk.XunitException("Protected work reached the helper.");
    }
}
