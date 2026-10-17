using System.Collections.ObjectModel;
using System.Globalization;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Health;
using CarsControl.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed record WuUpdateRow(WuUpdate Update, bool Hidden)
{
    public string Title => Update.Title;
    public string Size => Update.MaxDownloadBytes > 0 ? Converters.Format.Bytes(Update.MaxDownloadBytes) : "";
    public string Detail => string.Join(" · ", new[] { Update.KbArticle, Update.IsDriver ? "driver" : null, Size.Length > 0 ? Size : null }.Where(x => x is not null));
    public override string ToString() => Title;
}

public sealed record WuHistoryRow(string When, string Result, string Title, bool Failed)
{
    public override string ToString() => $"{When}: {Result}, {Title}";
}

/// <summary>
/// Windows Update's own controls in one place: pause, drivers, feature-update delay, active hours, hidden
/// updates and its history. Each setting is one of a few fixed ones the admin helper accepts
/// (<see cref="WindowsUpdatePolicy"/>); reading them and the history needs no admin.
/// </summary>
public sealed partial class WindowsUpdateViewModel : ObservableObject
{
    private readonly HelperClient _helper;
    private readonly CarsDb _db;
    private bool _loading;

    public ObservableCollection<WuUpdateRow> HiddenUpdates { get; } = [];
    public ObservableCollection<WuUpdateRow> Candidates { get; } = [];
    public ObservableCollection<WuHistoryRow> History { get; } = [];

    public IReadOnlyList<int> PauseChoices { get; } = [7, 14, 21, 35];
    public IReadOnlyList<int> DeferChoices { get; } = [0, 30, 60, 90, 180, 365];
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();

    [ObservableProperty] private string _pauseStatus = "";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _pausedByGaming;
    [ObservableProperty] private int _pauseDays = 14;
    [ObservableProperty] private bool _excludeDrivers;
    [ObservableProperty] private int _deferDays;
    [ObservableProperty] private int _activeFrom = 8;
    [ObservableProperty] private int _activeTo = 23;
    [ObservableProperty] private string _activeStatus = "";
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _searching;
    [ObservableProperty] private string _hiddenStatus = "";
    [ObservableProperty] private string _historyStatus = "";

    public WindowsUpdateViewModel(HelperClient helper, CarsDb db)
    {
        _helper = helper;
        _db = db;
    }

    /// <summary>Re-reads the settings (the tab calls it when shown).</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            var s = WindowsUpdatePolicy.Read();
            IsPaused = s.IsPaused(DateTime.UtcNow);
            PausedByGaming = s.PausedByGamingMode;
            PauseStatus = PausedByGaming ? "Gaming mode paused Windows Update while you play; it resumes by itself."
                : IsPaused ? $"Paused until {s.PausedUntilUtc!.Value.ToLocalTime():dddd d MMMM}. Nothing installs until then."
                : "Not paused: Windows installs updates as it normally does.";
            ExcludeDrivers = s.ExcludeDrivers;
            DeferDays = DeferChoices.Contains(s.DeferFeatureDays ?? 0) ? s.DeferFeatureDays ?? 0 : 0;
            if (s.ActiveHoursStart is { } a && s.ActiveHoursEnd is { } b)
            {
                ActiveFrom = a;
                ActiveTo = b;
                ActiveStatus = $"Windows won't restart for updates between {a:00}:00 and {b:00}:00.";
            }
            else
            {
                ActiveStatus = "Windows picks active hours from how you use the PC.";
            }
        }
        finally
        {
            _loading = false;
        }
        BackgroundTasks.Run(LoadHistoryAsync(), "Windows Update history");
        BackgroundTasks.Run(LoadHiddenAsync(), "hidden Windows updates");
    }

    private async Task<bool> ApplyAsync(string setting, string value, string title, string what)
    {
        if (_loading || IsBusy) return false;
        IsBusy = true;
        try
        {
            OpResult r;
            try
            {
                r = await _helper.SendAsync(new WindowsUpdatePolicyOp(setting, value), dryRun: HelperClient.DevUnelevated);
            }
            catch (HelperCancelledException)
            {
                Notice("Admin approval was cancelled, so nothing changed.", NoticeKind.Warning);
                return false;
            }
            catch (HelperUnavailableException ex)
            {
                Notice(ex.Message, NoticeKind.Error);
                return false;
            }
            _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "health", $"wu:{setting}", title, "Windows Update", null, null, true, r.Status.ToString(), r.ExitCode, what, r.Message));
            Notice(r.Ok ? what : r.Message ?? "Windows Update wasn't changed.", r.Ok ? NoticeKind.Success : NoticeKind.Error);
            return r.Ok;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private void Notice(string text, NoticeKind kind)
    {
        Banner = text;
        BannerKind = kind;
    }

    [RelayCommand]
    private Task PauseAsync() =>
        ApplyAsync("pause-until", DateTime.Now.AddDays(PauseDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Paused Windows Update", $"Windows Update is paused for {PauseDays} days.");

    [RelayCommand]
    private Task ResumeAsync() => ApplyAsync("pause-until", "resume", "Resumed Windows Update", "Windows Update is resumed.");

    partial void OnExcludeDriversChanged(bool value)
    {
        if (!_loading) _ = ApplyAsync("exclude-drivers", value ? "on" : "default", value ? "Keep drivers out of Windows Update" : "Allow drivers through Windows Update",
            value ? "Windows Update won't install drivers any more. Update them yourself (or here) when you choose." : "Windows Update may install drivers again.");
    }

    partial void OnDeferDaysChanged(int value)
    {
        if (!_loading) _ = ApplyAsync("defer-feature-days", value == 0 ? "default" : value.ToString(CultureInfo.InvariantCulture), "Feature update delay",
            value == 0 ? "Big yearly feature updates arrive when Microsoft offers them." : $"Big yearly feature updates will wait {value} days after Microsoft offers them.");
    }

    [RelayCommand]
    private Task SetActiveHoursAsync() =>
        ApplyAsync("active-hours", $"{ActiveFrom}-{ActiveTo}", "Active hours", $"Windows won't restart for updates between {ActiveFrom:00}:00 and {ActiveTo:00}:00.");

    [RelayCommand]
    private Task AutoActiveHoursAsync() => ApplyAsync("active-hours", "default", "Active hours", "Windows will choose active hours itself.");

    // ---------- hidden updates ----------

    private async Task LoadHiddenAsync()
    {
        try
        {
            var hidden = await WindowsUpdateApi.SearchHiddenAsync();
            HiddenUpdates.Clear();
            foreach (var u in hidden) HiddenUpdates.Add(new WuUpdateRow(u, true));
            HiddenStatus = hidden.Count == 0 ? "You haven't hidden any updates." : $"{hidden.Count} hidden update{(hidden.Count == 1 ? "" : "s")}: Windows won't offer them.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.InvalidComObjectException or InvalidOperationException)
        {
            HiddenStatus = "Windows Update didn't answer.";
        }
    }

    [RelayCommand]
    private async Task FindCandidatesAsync()
    {
        Searching = true;
        try
        {
            var found = await WindowsUpdateApi.SearchAsync();
            Candidates.Clear();
            foreach (var u in found) Candidates.Add(new WuUpdateRow(u, false));
            HiddenStatus = found.Count == 0 ? "Windows has no updates waiting." : $"{found.Count} update{(found.Count == 1 ? " is" : "s are")} waiting. Hide the ones you never want.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.InvalidComObjectException or InvalidOperationException)
        {
            HiddenStatus = "Windows Update didn't answer.";
        }
        finally
        {
            Searching = false;
        }
    }

    [RelayCommand]
    private Task HideAsync(WuUpdateRow? row) => SetHiddenAsync(row, hide: true);

    [RelayCommand]
    private Task ShowAgainAsync(WuUpdateRow? row) => SetHiddenAsync(row, hide: false);

    private async Task SetHiddenAsync(WuUpdateRow? row, bool hide)
    {
        if (row is null || IsBusy) return;
        IsBusy = true;
        try
        {
            OpResult r;
            try
            {
                r = await _helper.SendAsync(new HideUpdateOp(row.Update.UpdateId, hide), dryRun: HelperClient.DevUnelevated);
            }
            catch (HelperCancelledException)
            {
                Notice("Admin approval was cancelled, so nothing changed.", NoticeKind.Warning);
                return;
            }
            catch (HelperUnavailableException ex)
            {
                Notice(ex.Message, NoticeKind.Error);
                return;
            }
            _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "health", $"wu-hide:{row.Update.UpdateId}", row.Title, "Windows Update", null, null, true, r.Status.ToString(), r.ExitCode,
                hide ? "Hid a Windows update" : "Showed a hidden Windows update", r.Message));
            Notice(r.Ok ? (hide ? $"Hidden: Windows won't offer “{row.Title}”." : $"“{row.Title}” can be offered again.") : r.Message ?? "That didn't work.", r.Ok ? NoticeKind.Success : NoticeKind.Error);
            if (r.Ok && hide) Candidates.Remove(row);
        }
        finally
        {
            IsBusy = false;
            await LoadHiddenAsync();
        }
    }

    // ---------- history ----------

    private async Task LoadHistoryAsync()
    {
        try
        {
            var entries = await WindowsUpdateApi.HistoryAsync(80);
            History.Clear();
            // Store apps log the same line several times a day: one row per title per day.
            foreach (var e in entries.GroupBy(x => (x.Date.Date, x.Title, x.Result)).Select(g => g.First()).Take(40))
                History.Add(new WuHistoryRow(e.Date.ToString("d MMM", CultureInfo.CurrentCulture), e.IsUninstall ? "Removed" : e.ResultText, e.Title, e.Result is 4 or 5));
            HistoryStatus = History.Count == 0 ? "Windows Update has no history on this PC." : "";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.InvalidComObjectException or InvalidOperationException)
        {
            HistoryStatus = "Windows Update didn't answer.";
        }
    }
}
