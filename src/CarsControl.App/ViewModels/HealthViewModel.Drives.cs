using System.Collections.ObjectModel;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Health;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class HealthViewModel
{
    public ObservableCollection<DriveRow> DriveRows { get; } = [];

    [ObservableProperty] private string _driveStatus = "";
    [ObservableProperty] private bool _driveBusy;
    [ObservableProperty] private bool _driveCountersRead;

    private async Task LoadDrivesAsync()
    {
        var fresh = await Task.Run(() => DriveHealthReader.Read(includeCounters: false));
        // Counters read earlier (with admin) stay until read again; the plain read doesn't have them.
        _drives = fresh.Select(f => _drives.FirstOrDefault(d => d.Name == f.Name && d.LettersText == f.LettersText && d.HasCounters) is { } old ? f.WithCountersOf(old) : f).ToList();
        ShowDrives();
    }

    private void ShowDrives()
    {
        DriveRows.Clear();
        foreach (var d in _drives) DriveRows.Add(new DriveRow(d, DriveHealthRules.Evaluate(d)));
        DriveCountersRead = _drives.Any(d => d.HasCounters);
        if (!DriveCountersRead) DriveStatus = "Windows reports each drive as healthy or not. Temperature, wear and error counts are only shared with administrators.";
    }

    [RelayCommand]
    private async Task ReadDriveCountersAsync()
    {
        DriveBusy = true;
        try
        {
            DriveStatus = "Reading each drive's counters…";
            var r = await AdminAsync(new DriveHealthOp());
            if (r is null) { DriveStatus = ""; return; }
            if (!r.Ok) { DriveStatus = r.Message ?? "Windows didn't give the counters."; return; }
            if (r.Data is null) { DriveStatus = HelperClient.DevUnelevated ? "Test copy: admin reads are dry runs." : "No data came back."; return; }

            _drives = HelperJson.Deserialize<List<DriveReading>>(r.Data) ?? [];
            ShowDrives();
            DriveStatus = $"Read {DateTime.Now:h:mm tt}.";

            // Anything wrong goes to your phone too (once a day per drive).
            foreach (var row in DriveRows.Where(x => x.Verdict.Level != DriveLevel.Good))
                if (row.Drive.Letters.Count > 0 && row.Drive.Letters[0].Length > 0)
                    await _alerts.ReportDriveHealthAsync(row.Drive.Letters[0][0], $"{row.Summary}: {row.Notes}");
            BuildOverview();
        }
        finally
        {
            DriveBusy = false;
        }
    }
}
