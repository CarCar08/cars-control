using System.Collections.ObjectModel;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Health;
using CarsControl.Core.Help;
using CarsControl.Core.Optimize;
using CarsControl.Core.Tweaks;
using CommunityToolkit.Mvvm.ComponentModel;
using CarsControl.App.Views;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

/// <summary>Plain-English labels shared by the Optimization page's rows.</summary>
public static class OptimizeText
{
    public static string Evidence(Evidence e) => e switch
    {
        Core.Tweaks.Evidence.Documented => "Documented",
        Core.Tweaks.Evidence.Measured => "Measured",
        Core.Tweaks.Evidence.TestIt => "Depends on your PC",
        _ => "Your preference",
    };

    public static string GroupDescription(TweakGroup g) => g switch
    {
        TweakGroup.Power => "How hard the processor may work, and what happens when you shut down.",
        TweakGroup.Gaming => "Windows' own gaming features, set the way that suits how you play.",
        TweakGroup.Visuals => "Animations and see-through effects. On a modern PC these are a matter of taste.",
        TweakGroup.Background => "What starts when you sign in.",
        _ => "How Windows uses your internet connection for its own downloads.",
    };
}

/// <summary>A power mode choice in the drop-down.</summary>
public sealed record PowerModeOption(PowerMode Mode, string Title)
{
    public override string ToString() => Title;
}

/// <summary>
/// The Optimization page (1.1): catalog switches with an evidence label, Windows' power mode, hibernation and animations. Every
/// change can be undone; "Apply recommended" previews what it will change first. Nothing here touches security.
/// </summary>
public sealed partial class OptimizeViewModel : ObservableObject, ITweakRowOwner
{
    private const string PlanBeforeKey = "optimize_plan_before";
    private readonly TweakSwitcher _switcher;
    private readonly HelperClient _helper;
    private readonly CarsDb _db;
    private readonly AppSettings _settings;
    private readonly GameWatcher _games;

    public ObservableCollection<TweakRowViewModel> PowerRows { get; } = [];
    public ObservableCollection<TweakRowViewModel> GamingRows { get; } = [];
    public ObservableCollection<TweakRowViewModel> VisualRows { get; } = [];
    public ObservableCollection<TweakRowViewModel> BackgroundRows { get; } = [];
    public ObservableCollection<TweakRowViewModel> NetworkRows { get; } = [];

    public IReadOnlyList<PowerModeOption> PowerModes { get; } =
        [.. Enum.GetValues<PowerMode>().Select(m => new PowerModeOption(m, SystemSwitches.Describe(m)))];

    [ObservableProperty] private PowerModeOption? _selectedPowerMode;
    [ObservableProperty] private bool _powerModeAvailable;
    [ObservableProperty] private string _powerPlanText = "";
    [ObservableProperty] private string? _restorePlanText;
    [ObservableProperty] private bool _animationsOn;
    [ObservableProperty] private bool _hibernationOn;
    [ObservableProperty] private string _hibernationText = "";
    [ObservableProperty] private bool _hibernationBusy;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _pendingTestText;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecommendedCommand))]
    private bool _isBusy;

    private bool _syncing;

    public OptimizeViewModel(TweakSwitcher switcher, HelperClient helper, CarsDb db, AppSettings settings, GameWatcher games)
    {
        _games = games;
        _switcher = switcher;
        _helper = helper;
        _db = db;
        _settings = settings;
        foreach (var t in TweakCatalog.Available(Environment.OSVersion.Version.Build).Where(t => t.IsOptimization))
            RowsFor(t.Group).Add(new TweakRowViewModel(t, this));
    }

    private ObservableCollection<TweakRowViewModel> RowsFor(TweakGroup g) => g switch
    {
        TweakGroup.Power => PowerRows,
        TweakGroup.Gaming => GamingRows,
        TweakGroup.Visuals => VisualRows,
        TweakGroup.Background => BackgroundRows,
        _ => NetworkRows,
    };

    private IEnumerable<TweakRowViewModel> AllRows => PowerRows.Concat(GamingRows).Concat(VisualRows).Concat(BackgroundRows).Concat(NetworkRows);

    public static string PowerModeTip =>
        "Windows' own power mode (Settings › System › Power). Best performance lets the processor run at higher speeds sooner, " +
        "at the cost of more heat, fan noise and, on a laptop, battery. Best power efficiency does the opposite.";

    public Recommendation? PowerModeRecommendation => RecommendationContext.Current switch
    {
        PcType.GamingFirst => new Recommendation("optimize:power-mode", "Best performance", true, "Games get the highest processor speeds without waiting for Windows to ramp up."),
        PcType.Laptop => new Recommendation("optimize:power-mode", "Balanced", true, "Balanced still speeds up when needed and keeps the battery and fans in check."),
        _ => null,
    };

    public Recommendation? HibernationRecommendation => RecommendationContext.For("optimize:hibernation-off") is { } r
        ? r with { On = !r.On } // the switch shows hibernation itself, the recommendation is about turning it off
        : null;

    /// <summary>Reads everything live (the page calls it when shown).</summary>
    [RelayCommand]
    public void Refresh()
    {
        foreach (var row in AllRows) ReadRow(row);
        _syncing = true;
        var mode = SystemSwitches.ReadPowerMode();
        PowerModeAvailable = mode is not null;
        SelectedPowerMode = mode is { } m ? PowerModes.First(p => p.Mode == m) : null;
        var plan = SystemSwitches.ReadActivePlan();
        PowerPlanText = PowerModeAvailable ? "" :
            $"Not available right now: Windows only offers power modes on its Balanced plan, and this PC uses {(plan is { } p ? $"\"{p.Name}\"" : "another plan")}. " +
            "Switching to Balanced brings them back; Balanced with Best performance runs as fast and idles cooler.";
        RestorePlanText = _db.GetSetting(PlanBeforeKey) is { } before && Guid.TryParse(before, out var beforeId) && plan is { } now && now.Id != beforeId
            ? "Back to the previous plan" : null;
        AnimationsOn = SystemSwitches.ReadAnimations();
        var (hib, bytes) = SystemSwitches.ReadHibernation();
        HibernationOn = hib;
        _syncing = false;
        HibernationText = hib
            ? $"On. Its file takes {(bytes > 0 ? Format.Bytes(bytes) : "space")} on the system drive, reserved for when the PC hibernates."
            : "Off: no hibernation file. Fast startup is off too, since it depends on hibernation.";
        PendingTestText = FrameTestViewModel.ReadPending(_db) is { } pending && AllRows.FirstOrDefault(r => r.Tweak.Id == pending.TweakId) is { } waiting
            ? $"A test of \"{waiting.Title}\" is waiting: start {pending.GameFile} and record the second minute."
            : null;
        UpdateSummary();
    }

    // ---------- Test it on this PC ----------

    public void Test(TweakRowViewModel row) => FrameTestWindow.Open(new FrameTestViewModel(row, this, _helper, _games, _db));

    [RelayCommand]
    private void ContinueTest()
    {
        if (FrameTestViewModel.ReadPending(_db) is not { } pending || AllRows.FirstOrDefault(r => r.Tweak.Id == pending.TweakId) is not { } row) return;
        FrameTestWindow.Open(new FrameTestViewModel(row, this, _helper, _games, _db, pending));
        Refresh();
    }

    [RelayCommand]
    private void ForgetTest()
    {
        _db.SetSetting(FrameTestViewModel.PendingKey, "");
        Refresh();
    }

    private void ReadRow(TweakRowViewModel row) => _switcher.ReadInto(row);

    private void UpdateSummary()
    {
        var pending = Pending().Count;
        Summary = pending == 0 ? "Everything matches the recommendations for " + Recommendations.Describe(RecommendationContext.Current) + "."
            : $"{pending} change{(pending == 1 ? "" : "s")} recommended for {Recommendations.Describe(RecommendationContext.Current)}.";
        ApplyRecommendedCommand.NotifyCanExecuteChanged();
    }

    // ---------- switch rows ----------

    public async Task ChangeAsync(TweakRowViewModel row, bool on)
    {
        await _switcher.RunRowAsync(row, on ? "apply" : "undo");
        UpdateSummary();
    }

    public async Task ResetToDefaultAsync(TweakRowViewModel row)
    {
        await _switcher.RunRowAsync(row, "default");
        UpdateSummary();
    }

    // ---------- power mode, animations, hibernation ----------

    partial void OnSelectedPowerModeChanged(PowerModeOption? value)
    {
        if (_syncing || value is null) return;
        if (!SystemSwitches.SetPowerMode(value.Mode)) SetBanner("Windows didn't change the power mode.", NoticeKind.Error);
        RecordSimple("power-mode", "Power mode", $"Set to {value.Title}");
        Refresh();
    }

    /// <summary>Power modes only exist on the Balanced plan: switch to it, remembering the plan to go back to.</summary>
    [RelayCommand]
    private void UseBalancedPlan()
    {
        if (SystemSwitches.ReadActivePlan() is { } current && current.Id != SystemSwitches.BalancedPlanId) _db.SetSetting(PlanBeforeKey, current.Id.ToString());
        if (!SystemSwitches.SetActivePlan(SystemSwitches.BalancedPlanId)) SetBanner("Windows didn't switch to the Balanced plan.", NoticeKind.Error);
        else RecordSimple("power-plan", "Power plan", "Switched to Balanced");
        Refresh();
    }

    [RelayCommand]
    private void RestorePlan()
    {
        if (_db.GetSetting(PlanBeforeKey) is not { } before || !Guid.TryParse(before, out var id)) return;
        if (!SystemSwitches.SetActivePlan(id)) SetBanner("Windows didn't switch back; the plan may have been deleted.", NoticeKind.Error);
        else
        {
            _db.SetSetting(PlanBeforeKey, "");
            RecordSimple("power-plan", "Power plan", "Switched back to the previous plan");
        }
        Refresh();
    }

    partial void OnAnimationsOnChanged(bool value)
    {
        if (_syncing) return;
        if (!SystemSwitches.SetAnimations(value)) SetBanner("Windows didn't change the animation setting.", NoticeKind.Error);
        RecordSimple("animations", "Window and menu animations", value ? "Turned on" : "Turned off");
    }

    partial void OnHibernationOnChanged(bool value)
    {
        if (_syncing) return;
        BackgroundTasks.Run(SetHibernationAsync(value), "hibernation");
    }

    private async Task<bool> SetHibernationAsync(bool on)
    {
        HibernationBusy = true;
        try
        {
            var r = await _helper.SendAsync(new HibernationOp(on), dryRun: HelperClient.DevUnelevated);
            if (!r.Ok) SetBanner(r.Message ?? "Windows didn't change hibernation.", NoticeKind.Error);
            else RecordSimple("hibernation", "Hibernation", on ? "Turned on" : "Turned off (hibernation file deleted)");
            return r.Ok;
        }
        catch (HelperCancelledException)
        {
            SetBanner("Admin approval was cancelled, so hibernation didn't change.", NoticeKind.Warning);
            return false;
        }
        catch (HelperUnavailableException ex)
        {
            SetBanner(ex.Message, NoticeKind.Error);
            return false;
        }
        finally
        {
            HibernationBusy = false;
            Refresh();
        }
    }

    private void RecordSimple(string id, string title, string what) =>
        _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "optimize", "optimize:" + id, title, "Optimization", null, null,
            id == "hibernation", "Succeeded", 0, what, null));

    // ---------- apply recommended ----------

    /// <summary>One planned change, shown in the preview before anything happens.</summary>
    public sealed record PlannedChange(string Text, Func<Task<bool>> Run, bool NeedsAdmin);

    private List<PlannedChange> Pending()
    {
        var list = new List<PlannedChange>();
        foreach (var row in AllRows)
        {
            if (row.HasCaution || row.Recommendation is not { } rec || rec.On == row.IsOn) continue;
            var on = rec.On;
            list.Add(new PlannedChange($"{(on ? "Turn on" : "Turn off")}: {row.Title}", async () =>
            {
                var r = await _switcher.SetAsync(row.Tweak, on ? "apply" : "undo");
                if (r.Ok) _switcher.Record(row.Tweak, on ? "Turned on (recommended)" : "Undone (recommended)");
                else row.Status = r.Message;
                return r.Ok;
            }, row.NeedsAdmin));
        }
        if (PowerModeAvailable && PowerModeRecommendation is { } pm && SelectedPowerMode?.Title != pm.Title)
        {
            var mode = PowerModes.First(p => p.Title == pm.Title);
            list.Add(new PlannedChange($"Power mode: {pm.Title}", () =>
            {
                SelectedPowerMode = mode;
                return Task.FromResult(true);
            }, false));
        }
        if (HibernationRecommendation is { } hib && hib.On != HibernationOn)
            list.Add(new PlannedChange(hib.On ? "Turn hibernation on" : "Turn hibernation off (frees its file)", () => SetHibernationAsync(hib.On), true));
        return list;
    }

    private bool CanApplyRecommended() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanApplyRecommended))]
    private async Task ApplyRecommendedAsync()
    {
        var plan = Pending();
        if (plan.Count == 0)
        {
            SetBanner("Everything already matches the recommendations for " + Recommendations.Describe(RecommendationContext.Current) + ".", NoticeKind.Success);
            return;
        }
        var text = $"For {Recommendations.Describe(RecommendationContext.Current)} (change this in Settings › This PC):{Environment.NewLine}{Environment.NewLine}"
            + string.Join(Environment.NewLine, plan.Select(p => "• " + p.Text + (p.NeedsAdmin ? " (admin)" : "")))
            + $"{Environment.NewLine}{Environment.NewLine}Every change can be switched back on this page.";
        if (!await Dialogs.ConfirmAsync(text, "Apply", "Cancel", "Apply recommended optimizations")) return;

        IsBusy = true;
        Banner = null;
        var done = 0;
        var restoreNote = "";
        try
        {
            if (plan.Any(p => p.NeedsAdmin))
            {
                var rp = await SafetyNet.MakeAsync(_helper, _settings.Store.Current.Health, SafetyNet.Read(), RiskyChange.OptimizationBatch, $"{plan.Count} changes", HelperClient.DevUnelevated);
                if (rp.Outcome == RestoreOutcome.Failed) restoreNote = " No restore point could be made, but each change can still be switched back.";
            }
            foreach (var change in plan.OrderBy(p => p.NeedsAdmin))
                if (await change.Run()) done++;
            SetBanner($"Applied {done} of {plan.Count}. Each one can be switched back on this page." + restoreNote, done == plan.Count ? NoticeKind.Success : NoticeKind.Warning);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private void SetBanner(string text, NoticeKind kind)
    {
        Banner = text;
        BannerKind = kind;
    }
}
