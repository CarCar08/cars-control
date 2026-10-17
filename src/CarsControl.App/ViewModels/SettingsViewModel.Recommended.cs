using System.Reflection;
using CarsControl.App.Services;
using CarsControl.Core.Help;
using CarsControl.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed record PcTypeOption(PcType Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Settings › This PC › kind of PC, and "Apply recommended settings".</summary>
public sealed partial class SettingsViewModel
{
    private Dictionary<PropertyInfo, bool>? _recommendedUndo;

    [RelayCommand]
    private async Task UndoRecommendedAsync()
    {
        if (_recommendedUndo is null) { Message = "No recommended-settings batch to undo in this session."; return; }
        if (!await Dialogs.ConfirmAsync("Restore the exact switch values from before the last Apply recommended batch?", "Undo")) return;
        foreach (var (property, value) in _recommendedUndo) property.SetValue(this, value);
        _recommendedUndo = null;
        CanUndoRecommended = false;
        Message = "Restored the switch values from before the last recommended-settings batch.";
    }

    [RelayCommand]
    private async Task UndoFirstRunAsync()
    {
        if (_settings.Store.Current.FirstRunUndo is not { } undo) { Message = "There are no saved setup choices to undo."; return; }
        var changes = FirstRunSetup.Preview(FirstRunSetup.Read(_settings.Store.Current, _settings.StartWithWindows), undo.Choices);
        if (!await Dialogs.ConfirmAsync(string.Join("\n", changes) + "\n\nRestore the previous setup settings and hold reasons? Privacy switches have their own Undo on Privacy. Backup jobs, sync folders and installed drivers are kept.", "Undo setup settings")) return;
        if (undo.StartupCommand is not null || !undo.Choices.StartWithWindows) _settings.StartupCommand = undo.StartupCommand;
        else if (_settings.StartWithWindows != undo.Choices.StartWithWindows) _settings.StartWithWindows = undo.Choices.StartWithWindows;
        _settings.Store.Update(s => { FirstRunSetup.Restore(s, undo); s.FirstRunUndo = null; });
        Reload();
        Message = "Restored the settings from before the first-time setup.";
    }
    public IReadOnlyList<PcTypeOption> PcTypeOptions { get; } =
    [
        new(PcType.Auto, "Detect it (" + (RecommendationContext.HasBattery ? "a laptop" : "a desktop PC") + ")"),
        new(PcType.Desktop, "Desktop PC"),
        new(PcType.Laptop, "Laptop"),
        new(PcType.GamingFirst, "Gaming PC (games come first)"),
    ];

    [ObservableProperty] private PcTypeOption? _pcTypeChoice;

    private void ReloadPcType()
    {
        var chosen = _settings.Store.Current.Profile.PcType;
        _loadingPcType = true;
        PcTypeChoice = PcTypeOptions.FirstOrDefault(o => o.Value == chosen) ?? PcTypeOptions[0];
        _loadingPcType = false;
        RecommendationContext.Set(chosen);
        CanUndoFirstRun = _settings.Store.Current.FirstRunUndo is not null;
    }

    /// <summary>The Undo buttons only show when there's something to undo.</summary>
    [ObservableProperty] private bool _canUndoRecommended;
    [ObservableProperty] private bool _canUndoFirstRun;

    private bool _loadingPcType;

    partial void OnPcTypeChoiceChanged(PcTypeOption? value)
    {
        if (value is null || _loadingPcType) return;
        _settings.Store.Update(s => s.Profile.PcType = value.Value);
        RecommendationContext.Set(value.Value);
        OnPropertyChanged(nameof(RecommendedSummary));
    }

    public string RecommendedSummary => $"Recommended badges are for {Recommendations.Describe(RecommendationContext.Current)}.";

    /// <summary>The switches that differ from the recommendation, with their current and recommended values.</summary>
    private List<(Recommendation Rec, PropertyInfo Property)> Differences()
    {
        var list = new List<(Recommendation, PropertyInfo)>();
        foreach (var rec in Recommendations.AllFor(RecommendationContext.Current))
        {
            var prop = GetType().GetProperty(rec.Id, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null || prop.PropertyType != typeof(bool) || !prop.CanWrite) continue; // e.g. RestartAway lives on Health › Overview
            if ((bool)prop.GetValue(this)! != rec.On) list.Add((rec, prop));
        }
        return list;
    }

    [RelayCommand]
    private async Task ApplyRecommendedAsync()
    {
        var diff = Differences();
        if (diff.Count == 0)
        {
            await Dialogs.InfoAsync($"Everything here already matches what's recommended for {Recommendations.Describe(RecommendationContext.Current)}.", "Recommended settings");
            return;
        }
        var lines = string.Join("\n", diff.Select(d => $"• {d.Rec.Title}: turn {(d.Rec.On ? "on" : "off")}"));
        if (!await Dialogs.ConfirmAsync(
                $"These {diff.Count} settings differ from what's recommended for {Recommendations.Describe(RecommendationContext.Current)}:\n\n{lines}\n\nEach one can be switched back here afterwards.",
                "Apply", title: "Apply recommended settings")) return;
        _recommendedUndo = diff.ToDictionary(d => d.Property, d => (bool)d.Property.GetValue(this)!);
        foreach (var (rec, prop) in diff) prop.SetValue(this, rec.On);
        CanUndoRecommended = true;
        Message = $"Applied {diff.Count} recommended setting{(diff.Count == 1 ? "" : "s")}.";
    }
}
