using System.Collections.ObjectModel;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Cleanup;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class CleanupRowViewModel(CleanupRule rule) : ObservableObject
{
    public CleanupRule Rule { get; } = rule;
    public string Title => Rule.Title;
    public CarsControl.Core.Help.Recommendation Recommendation => CarsControl.Core.Help.Recommendations.ForCleanup(Rule.Id, Rule.Title, Rule.DefaultOn);
    public string Description => Rule.Description;
    public string? Warning => Rule.Warning;
    public bool Elevated => Rule.Elevated;
    public ObservableCollection<string> Items { get; } = [];
    public bool HasItems => Items.Count > 0;
    public string ItemsHeader => $"Show the {Items.Count} item(s)";

    [ObservableProperty] private bool _isSelected = rule.DefaultOn;
    [ObservableProperty] private long _bytes = -1;
    [ObservableProperty] private long _itemCount;
    [ObservableProperty] private string _estimateText = "Measuring…";
    [ObservableProperty] private string? _note;
    [ObservableProperty] private string? _result;
    [ObservableProperty] private bool _isRunning;

    public void NotifyItems()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ItemsHeader));
    }
}

public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly HelperClient _helper;
    private readonly CleanupRunner _runner;

    public ObservableCollection<CleanupRowViewModel> UserRows { get; } = [];
    public ObservableCollection<CleanupRowViewModel> AdminRows { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    private bool _isBusy;
    [ObservableProperty] private bool _dryRun;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;

    public CleanupViewModel(HelperClient helper, CleanupRunner runner)
    {
        _helper = helper;
        _runner = runner;
        foreach (var r in CleanupRules.All)
        {
            var row = new CleanupRowViewModel(r);
            row.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(CleanupRowViewModel.IsSelected) or nameof(CleanupRowViewModel.Bytes)) UpdateSummary(); };
            (r.Elevated ? AdminRows : UserRows).Add(row);
        }

        // Runs started elsewhere (scheduled maintenance, approvals) show here too.
        _runner.RunningChanged += running => IsBusy = running;
        _runner.RuleStarted += rule => { if (Row(rule) is { } row) row.IsRunning = true; };
        _runner.RuleFinished += r =>
        {
            if (Row(r.Rule) is not { } row) return;
            row.IsRunning = false;
            row.Result = r.Text;
        };
        _runner.RunFinished += s =>
        {
            Banner = s.DryRun ? "Dry run — nothing was deleted. Each item shows what it would free."
                : (s.Origin == "Cleanup" ? "" : $"{s.Origin}: ") + $"Done — freed {Format.Bytes(s.FreedBytes)}."
                  + (s.Waiting.Count > 0 ? $" {s.Waiting.Count} system item(s) wait for your approval (Home)." : "");
            BannerKind = s.DryRun ? NoticeKind.Info : NoticeKind.Success;
            if (!s.DryRun) _estimatedAt = null; // sizes really changed
        };
    }

    private CleanupRowViewModel? Row(CleanupRule rule) => All.FirstOrDefault(r => r.Rule.Id == rule.Id);

    private IEnumerable<CleanupRowViewModel> All => UserRows.Concat(AdminRows);

    private DateTime? _estimatedAt;
    private Task? _estimating;

    /// <summary>Raised after estimates change (Home shows the reclaimable total).</summary>
    public event Action? EstimatesChanged;

    /// <summary>Re-measures only when the last estimate is over 10 minutes old (it used to re-read
    /// ~25k cache files every time you opened the page).</summary>
    public Task EnsureEstimatesAsync()
    {
        if (_estimating is { IsCompleted: false }) return _estimating;
        if (_estimatedAt is { } t && DateTime.Now - t < TimeSpan.FromMinutes(10)) return Task.CompletedTask;
        return _estimating = EstimateAsync();
    }

    /// <summary>Default-ticked, per-user categories: what "Clean selected" frees without admin or risk.</summary>
    public (long Bytes, long Items, int Rules) DefaultReclaimable()
    {
        var rows = UserRows.Where(r => r.Rule.DefaultOn && r.Bytes > 0).ToList();
        return (rows.Sum(r => r.Bytes), rows.Sum(r => r.ItemCount), rows.Count);
    }

    [RelayCommand]
    public async Task EstimateAsync()
    {
        IsBusy = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // All categories in parallel; each touches a different folder.
            var results = await Task.WhenAll(UserRows.Select(row => Task.Run(() =>
            {
                var est = CleanupRules.Estimate(row.Rule);
                var items = row.Rule.ListItems?.Invoke();
                return (row, est, items);
            })));
            foreach (var (row, est, items) in results)
            {
                Apply(row, est);
                if (items is not null)
                {
                    row.Items.Clear();
                    foreach (var i in items) row.Items.Add(i);
                    row.NotifyItems();
                }
            }
            if (_helper.IsConnected) await EstimateAdminAsync();
            else foreach (var row in AdminRows) row.EstimateText = "Not measured yet (needs admin)";
            _estimatedAt = DateTime.Now;
            PerfLog.Duration("cleanup estimates", sw.Elapsed);
        }
        finally
        {
            IsBusy = false;
            UpdateSummary();
            EstimatesChanged?.Invoke();
        }
    }

    /// <summary>Admin folders aren't readable as you, so their size needs the helper (one UAC prompt if no session yet).</summary>
    [RelayCommand]
    private async Task EstimateAdminAsync()
    {
        try
        {
            var r = await _helper.SendAsync(new CleanupEstimateOp(AdminRows.Select(a => a.Rule.Id).ToList()));
            if (r.Ok && r.Data is { } json && HelperJson.Deserialize<List<CleanupEstimate>>(json) is { } list)
                foreach (var e in list)
                    if (AdminRows.FirstOrDefault(a => a.Rule.Id == e.RuleId) is { } row) Apply(row, e);
        }
        catch (HelperCancelledException)
        {
            Banner = "Admin approval was cancelled, so admin items weren't measured.";
            BannerKind = NoticeKind.Warning;
        }
        catch (Exception ex)
        {
            Banner = $"Couldn't measure admin items: {ex.Message}";
            BannerKind = NoticeKind.Error;
        }
        UpdateSummary();
    }

    private static void Apply(CleanupRowViewModel row, CleanupEstimate e)
    {
        row.Bytes = e.Bytes;
        row.ItemCount = e.Items;
        row.EstimateText = e.Note is { } n && e.Bytes == 0 ? n
            : e.Items == 0 ? "Nothing to clean"
            : $"{Format.Bytes(e.Bytes)} · {e.Items:N0} item(s)";
        row.Note = e.Bytes > 0 ? e.Note : null;
    }

    private void UpdateSummary()
    {
        var sel = All.Where(r => r.IsSelected).ToList();
        var known = sel.Where(r => r.Bytes > 0).Sum(r => r.Bytes);
        var admin = sel.Count(r => r.Elevated);
        Summary = $"{sel.Count} selected · about {Format.Bytes(known)}" + (admin > 0 ? $" · {admin} need admin" : "");
        CleanCommand.NotifyCanExecuteChanged();
    }

    private bool CanClean() => !IsBusy && All.Any(r => r.IsSelected);

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        Banner = null;
        var summary = await _runner.RunAsync(All.Where(r => r.IsSelected).Select(r => r.Rule).ToList(), "Cleanup", DryRun);
        if (summary is { DryRun: false }) await EstimateAsync(); // sizes really changed: always re-measure
    }
}
