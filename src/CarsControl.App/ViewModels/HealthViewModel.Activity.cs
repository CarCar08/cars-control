using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Core.Health;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class HealthViewModel
{
    private DispatcherTimer? _activityTimer;
    private bool _sampling;
    private CancellationTokenSource? _activityCancellation;
    private int _activityEpoch;

    public ObservableCollection<AppUseRow> ActivityRows { get; } = [];

    [ObservableProperty] private ActivitySort _sortBy = ActivitySort.Cpu;
    [ObservableProperty] private string _activityTotals = "";
    [ObservableProperty] private bool _activityPaused;

    public IReadOnlyList<SortChoice> SortChoices { get; } = [new("Processor", ActivitySort.Cpu), new("Graphics", ActivitySort.Gpu), new("Memory", ActivitySort.Ram), new("Disk", ActivitySort.Disk)];

    partial void OnSortByChanged(ActivitySort value) => ShowActivity();

    private ActivitySnapshot? _activity;

    private void StartActivity()
    {
        _activityCancellation = new CancellationTokenSource();
        BackgroundTasks.Run(SampleActivityAsync(), "activity sampling");
        _activityTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _activityTimer.Tick += async (_, _) =>
        {
            if (!ActivityPaused) await SampleActivityAsync();
        };
        _activityTimer.Start();
    }

    private void StopActivity()
    {
        _activityEpoch++;
        _activityCancellation?.Cancel();
        _activityCancellation?.Dispose();
        _activityCancellation = null;
        _activityTimer?.Stop();
        _activityTimer = null;
    }

    private async Task SampleActivityAsync()
    {
        if (_sampling) return;
        _sampling = true;
        var epoch = _activityEpoch;
        var token = _activityCancellation?.Token ?? CancellationToken.None;
        try
        {
            var reading = await ActivityReader.SampleAsync(token);
            if (epoch != _activityEpoch || !_pageVisible || SelectedTab != HealthTab.Activity) return;
            _activity = reading;
            ShowActivity();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorReporter.Log("activity", ex);
        }
        finally
        {
            _sampling = false;
        }
    }

    private void ShowActivity()
    {
        if (_activity is not { } a) return;
        ActivityRows.Clear();
        // Rows that aren't using the chosen resource at all are noise (memory always counts).
        foreach (var app in a.TopApps(SortBy, 60).Where(x => SortBy == ActivitySort.Ram || (SortBy == ActivitySort.Cpu ? x.CpuPercent > 0 : SortBy == ActivitySort.Gpu ? x.GpuPercent > 0 : x.DiskBytesPerSec > 0)).Take(25))
            ActivityRows.Add(new AppUseRow(app, $"{app.CpuPercent:0.#}%", app.GpuPercent > 0 ? $"{app.GpuPercent:0.#}%" : "—", Format.Bytes(app.RamBytes),
                app.DiskBytesPerSec > 0 ? $"{Format.Bytes(app.DiskBytesPerSec)}/s" : "—"));
        ActivityTotals = $"Processor {a.TotalCpuPercent:0}%  ·  " + (a.TotalGpuPercent is { } g ? $"Graphics {g:0}%  ·  " : "") +
            $"Memory {Format.Bytes(a.RamUsedBytes)} of {Format.Bytes(a.RamTotalBytes)}";
    }

    [RelayCommand]
    private async Task EndAppAsync(AppUseRow? row)
    {
        if (row is not { CanEnd: true } || row.App.Pids.Count == 0) return;
        var n = row.App.Instances;
        if (!await Dialogs.ConfirmAsync($"Close {row.Name}{(n > 1 ? $" and its {n} processes" : "")}? Anything unsaved in it is lost.", "Close it", danger: true)) return;
        var ended = 0;
        foreach (var pid in row.App.Pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                ended++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or Windows refused: the next sample shows what's left.
            }
        }
        SetBanner(ended > 0 ? $"Closed {row.Name}." : $"{row.Name} couldn't be closed from here.", ended > 0 ? NoticeKind.Success : NoticeKind.Warning);
        await SampleActivityAsync();
    }

    [RelayCommand]
    private void OpenActivityFolder(AppUseRow? row) => Platform.ShowInFileManager(row?.Path);
}
