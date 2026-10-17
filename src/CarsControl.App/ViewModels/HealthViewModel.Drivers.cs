using System.Collections.ObjectModel;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Health;
using CarsControl.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class HealthViewModel
{
    private IReadOnlyList<InstalledDriver> _installedDrivers = [];

    public ObservableCollection<DriverRow> DriverRows { get; } = [];
    public ObservableCollection<DriverPackageRow> OldPackages { get; } = [];
    public ObservableCollection<DriverPackageRow> GpuPackages { get; } = [];

    [ObservableProperty] private string _driverFilter = "";
    [ObservableProperty] private string _driverSummary = "";
    [ObservableProperty] private string _storeSummary = "";
    [ObservableProperty] private bool _driversBusy;

    partial void OnDriverFilterChanged(string value) => ApplyDriverFilter();

    private async Task LoadDriversAsync()
    {
        if (DriversBusy) return;
        DriversBusy = true;
        try
        {
            var (drivers, inUse) = await Task.Run(DriverStore.ReadInstalled);
            _installedDrivers = drivers;
            ApplyDriverFilter();

            var pnputil = System.IO.Path.Combine(Tools.System32, "pnputil.exe");
            var listing = await ProcessRunner.RunAsync(pnputil, ["/enum-drivers"], null, TimeSpan.FromSeconds(60));
            var packages = DriverStore.Parse(listing.Lines, inUse);
            OldPackages.Clear();
            foreach (var p in DriverStore.Superseded(packages)) OldPackages.Add(new DriverPackageRow(p));
            GpuPackages.Clear();
            foreach (var p in packages.Where(x => x.ClassName.Equals("Display", StringComparison.OrdinalIgnoreCase) && x.Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(x => x.Date)) GpuPackages.Add(new DriverPackageRow(p));
            StoreSummary = packages.Count == 0
                ? "Couldn't list the driver store (Windows only does that in English)."
                : OldPackages.Count == 0
                    ? $"{packages.Count} driver packages are stored, and none is an unused older copy."
                    : $"{packages.Count} driver packages are stored; {OldPackages.Count} are older copies that no device uses. Windows keeps them after each update in case you roll back.";
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("drivers", ex);
            SetBanner("Couldn't read the drivers: " + ex.Message, NoticeKind.Warning);
        }
        finally
        {
            DriversBusy = false;
        }
    }

    private void ApplyDriverFilter()
    {
        var f = DriverFilter.Trim();
        var matches = _installedDrivers.Where(d => f.Length == 0
            || d.Device.Contains(f, StringComparison.OrdinalIgnoreCase) || d.Manufacturer.Contains(f, StringComparison.OrdinalIgnoreCase)
            || d.Class.Contains(f, StringComparison.OrdinalIgnoreCase) || d.Version.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        DriverRows.Clear();
        foreach (var d in matches.Take(500))
            DriverRows.Add(new DriverRow(d.Device, d.Manufacturer, d.Version, d.Date?.ToString("d MMM yyyy") ?? "", d.Signed, d.Class, d.InfName));
        DriverSummary = f.Length == 0 ? $"{_installedDrivers.Count} drivers for the devices in this PC." : $"{matches.Count} of {_installedDrivers.Count} drivers match.";
    }

    [RelayCommand]
    private void SelectAllOldPackages()
    {
        foreach (var p in OldPackages) p.IsSelected = true;
    }

    [RelayCommand]
    private async Task DeleteOldPackagesAsync()
    {
        var chosen = OldPackages.Where(p => p.IsSelected).ToList();
        if (chosen.Count == 0)
        {
            SetBanner("Tick the old driver packages to remove first.", NoticeKind.Info);
            return;
        }
        var list = string.Join("\n", chosen.Take(8).Select(p => $"• {p.Name} {p.Version} ({p.Date})")) + (chosen.Count > 8 ? $"\n• and {chosen.Count - 8} more" : "");
        if (!await Dialogs.ConfirmAsync($"Remove {chosen.Count} old driver package{(chosen.Count == 1 ? "" : "s")} from Windows' driver store?\n\n{list}\n\nThe newest copy of each driver stays, and Windows refuses to remove one a device still uses. You can't roll back to these after.", "Remove", danger: true)) return;

        DriversBusy = true;
        try
        {
            if (!await MakeRestorePointAsync(RiskyChange.DriverCleanup, $"{chosen.Count} old packages")) return;
            int removed = 0, refused = 0;
            foreach (var p in chosen)
            {
                var r = await AdminAsync(new DriverDeleteOp(p.Published));
                if (r is null) break; // cancelled or unavailable: stop asking
                if (r.Ok) removed++;
                else refused++;
                Record($"driver:{p.Published}", $"{p.Name} {p.Version}", r.Ok ? "Removed an old driver package" : "Windows kept this driver package", r);
            }
            SetBanner(refused == 0 ? $"Removed {removed} old driver package{(removed == 1 ? "" : "s")}." : $"Removed {removed}; Windows kept {refused} of them.", refused == 0 && removed > 0 ? NoticeKind.Success : NoticeKind.Warning);
        }
        finally
        {
            DriversBusy = false;
        }
        await LoadDriversAsync();
    }

    /// <summary>A restore point before a risky change, when you want one and System Protection is on.
    /// False when one was wanted and couldn't be made and you chose not to go on.</summary>
    private async Task<bool> MakeRestorePointAsync(RiskyChange change, string detail)
    {
        var r = await SafetyNet.MakeAsync(_helper, _settings.Store.Current.Health, SafetyNet.Read(), change, detail, HelperClient.DevUnelevated);
        switch (r.Outcome)
        {
            case RestoreOutcome.NoProtection:
                SetBanner("No restore point was made: System Protection is off (Overview › Safety net can turn it on).", NoticeKind.Info);
                return true;
            case RestoreOutcome.Cancelled:
                SetBanner("Admin approval was cancelled, so nothing was changed.", NoticeKind.Warning);
                return false;
            case RestoreOutcome.Failed:
                return await Dialogs.ConfirmAsync("A restore point couldn't be made, so there'd be nothing to go back to. Go on without one?", "Go on anyway", danger: true);
            default:
                return true;
        }
    }
}
