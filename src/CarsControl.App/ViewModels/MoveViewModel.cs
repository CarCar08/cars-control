using System.Collections.ObjectModel;
using System.IO;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Settings;
using CarsControl.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed record MoveTarget(string Path, string Label, long FreeBytes)
{
    public override string ToString() => FreeBytes > 0 ? $"{Label} · {Format.Bytes(FreeBytes)} free" : Label;
}

/// <summary>
/// The window that moves a Steam game to another library, or a folder to another drive (leaving a link). It first says
/// exactly what will happen, or what stops it; nothing is removed from the old place until the copy has been checked.
/// </summary>
public sealed partial class MoveViewModel : ObservableObject
{
    private readonly Func<MoveTarget, CancellationToken, Task<(string Plan, string? Blocker, Func<IProgress<MoveProgress>, CancellationToken, Task<MoveResult>>? Run)>> _plan;
    private readonly Action? _afterSuccess;
    private CancellationTokenSource? _cts;
    private Func<IProgress<MoveProgress>, CancellationToken, Task<MoveResult>>? _run;

    public ObservableCollection<MoveTarget> Targets { get; } = [];

    public string Title { get; }
    public string Subtitle { get; }

    [ObservableProperty] private MoveTarget? _selectedTarget;
    [ObservableProperty] private string _planText = "";
    [ObservableProperty] private string? _blocker;
    [ObservableProperty] private bool _isPlanning;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _stage = "";
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private bool _resultOk;
    [ObservableProperty] private bool _isDone;

    public event Action<bool>? CloseRequested;

    public bool CanStart => !IsRunning && !IsDone && !IsPlanning && Blocker is null && _run is not null;
    public bool HasTargets => Targets.Count > 0;
    public string CloseText => IsDone ? "Close" : "Cancel";

    private MoveViewModel(string title, string subtitle, IEnumerable<MoveTarget> targets,
        Func<MoveTarget, CancellationToken, Task<(string, string?, Func<IProgress<MoveProgress>, CancellationToken, Task<MoveResult>>?)>> plan, Action? afterSuccess)
    {
        Title = title;
        Subtitle = subtitle;
        _plan = plan;
        _afterSuccess = afterSuccess;
        foreach (var t in targets) Targets.Add(t);
        SelectedTarget = Targets.FirstOrDefault();
        if (SelectedTarget is null) Blocker = "There's nowhere to move it to.";
    }

    // ---------- the two kinds of move ----------

    public static MoveViewModel ForGame(GameEntry game, IReadOnlyList<GameLibrary> libraries, Action afterSuccess)
    {
        var known = libraries.Where(l => l.Source == "Steam").Select(l => l.Path).ToList();
        var targets = libraries.Where(l => l.Source == "Steam" && l.Online && !string.Equals(l.Path.TrimEnd('\\'), game.Library.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .Select(l => new MoveTarget(l.Path, l.Path, FreeOf(l.Path))).ToList();
        return new MoveViewModel($"Move {game.Name}", $"From {game.Library} to another Steam library. Steam has to be closed, and the game not running.", targets,
            (target, ct) => Task.Run<(string, string?, Func<IProgress<MoveProgress>, CancellationToken, Task<MoveResult>>?)>(() =>
            {
                var plan = SteamMover.Plan(game, target.Path, known, () => RunningPrograms.IsRunning("steam"), RunningPrograms.AnyUnder);
                return plan.CanRun
                    ? ($"Moves {Format.Bytes(plan.Bytes)}: the game, its Workshop files and its shader cache. Steam's record of where the game lives is switched over last, then the old copy is removed.",
                       (string?)null,
                       (Func<IProgress<MoveProgress>, CancellationToken, Task<MoveResult>>?)((progress, token) => SteamMover.RunAsync(plan, progress, token)))
                    : ("", plan.Blocker, null);
            }, ct), afterSuccess);
    }

    /// <summary>An Epic game to another drive (into "Epic Games" at its root). The old copy usually sits in Program Files, so removing it goes through the admin helper.</summary>
    public static MoveViewModel ForEpicGame(GameEntry game, Action afterSuccess)
    {
        var drive = Path.GetPathRoot(game.InstallPath ?? "") ?? "";
        var targets = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && !d.Name.Equals(drive, StringComparison.OrdinalIgnoreCase))
            .Select(d => new MoveTarget(d.Name, $"{d.Name.TrimEnd('\\')} {d.VolumeLabel}".Trim(), d.AvailableFreeSpace)).ToList();
        return new MoveViewModel($"Move {game.Name}", $"From {game.InstallPath} to another drive. The Epic Games Launcher has to be closed, and the game not running.", targets,
            (target, ct) => Task.Run<(string, string?, Func<IProgress<MoveProgress>, CancellationToken, Task<MoveResult>>?)>(() =>
            {
                var plan = EpicMover.Plan(game, target.Path, EpicMover.LauncherRunning, RunningPrograms.AnyUnder);
                return plan.CanRun
                    ? ((plan.EarlierCopyExtras > 0 ? $"{plan.To} already holds an earlier copy of this game: it is reused, and the {plan.EarlierCopyExtras} file{(plan.EarlierCopyExtras == 1 ? "" : "s")} the game created there (crash reports, logs) are removed. " : "") + $"Copies {Format.Bytes(plan.Bytes)} to {plan.To} and checks every file, then points the Epic launcher at the new place. The old copy stays until you've started the game from Epic; then “Remove old copy” on the Games tab frees the space.",
                       (string?)null,
                       (Func<IProgress<MoveProgress>, CancellationToken, Task<MoveResult>>?)((progress, token) => EpicMover.RunAsync(plan, progress, token)))
                    : ("", plan.Blocker, null);
            }, ct), afterSuccess);
    }

    public static MoveViewModel ForFolder(string folder, SettingsStore settings, Action afterSuccess)
    {
        var drive = Path.GetPathRoot(folder) ?? "";
        var targets = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && !d.Name.Equals(drive, StringComparison.OrdinalIgnoreCase))
            .Select(d => new MoveTarget(d.Name, $"{d.Name.TrimEnd('\\')} {d.VolumeLabel}".Trim(), d.AvailableFreeSpace)).ToList();
        return new MoveViewModel($"Move {Path.GetFileName(folder.TrimEnd('\\'))} to another drive", $"{folder}: moved, with a link left in its place, so everything that uses this path keeps working. “Move back” undoes it.", targets,
            (target, ct) => Task.Run<(string, string?, Func<IProgress<MoveProgress>, CancellationToken, Task<MoveResult>>?)>(() =>
            {
                if (FolderLinkMover.Refuse(folder, target.Path) is { } why) return ("", why, null);
                var destination = Path.Combine(target.Path, "Moved by CARS", Path.GetFileName(folder.TrimEnd('\\')));
                var bytes = TreeOps.Measure(folder).Bytes;
                return ($"Copies {Format.Bytes(bytes)} to {destination}, checks the copy, then replaces {folder} with a link to it.",
                    (string?)null,
                    (Func<IProgress<MoveProgress>, CancellationToken, Task<MoveResult>>?)(async (progress, token) =>
                    {
                        var result = await FolderLinkMover.MoveAsync(folder, destination, progress, token);
                        if (result.Ok) settings.Update(s => s.Storage.LinkedMoves.Add(new LinkedMoveRecord { Original = folder, NewLocation = destination, When = DateTime.Now, Bytes = result.BytesMoved }));
                        return result;
                    }));
            }, ct), afterSuccess);
    }

    private static long FreeOf(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; } catch (Exception ex) when (ex is IOException or ArgumentException) { return 0; }
    }

    // ---------- planning and running ----------

    partial void OnSelectedTargetChanged(MoveTarget? value) => _ = ReplanAsync(value);

    private CancellationTokenSource? _planCts;

    private async Task ReplanAsync(MoveTarget? target)
    {
        _planCts?.Cancel();
        _planCts = new CancellationTokenSource();
        if (target is null) return;
        IsPlanning = true;
        Blocker = null;
        PlanText = "Checking…";
        try
        {
            var (plan, blocker, run) = await _plan(target, _planCts.Token);
            if (target != SelectedTarget) return;
            PlanText = plan;
            Blocker = blocker;
            _run = run;
        }
        catch (OperationCanceledException)
        {
            // A newer choice replaced this one.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Blocker = ex.Message;
            _run = null;
        }
        finally
        {
            IsPlanning = false;
            OnPropertyChanged(nameof(CanStart));
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnBlockerChanged(string? value) => OnPropertyChanged(nameof(CanStart));
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanStart));
    partial void OnIsDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CloseText));
    }
    partial void OnIsPlanningChanged(bool value) => OnPropertyChanged(nameof(CanStart));

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_run is null || SelectedTarget is not { } target) return;
        if (!await Dialogs.ConfirmAsync($"{Title}?\n\n{PlanText}\n\nKeep the PC on until it finishes. If it's interrupted, nothing is lost: the original stays where it was, and the next try carries on from what was copied.", "Move it")) return;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<MoveProgress>(p =>
        {
            Percent = p.Percent;
            Stage = p.Stage + (p.BytesTotal > 0 ? $" · {Format.Bytes(p.BytesDone)} of {Format.Bytes(p.BytesTotal)}" : "");
        });
        try
        {
            var result = await _run(progress, _cts.Token);
            ResultOk = result.Ok;
            ResultText = result.Message;
            if (result.Ok) _afterSuccess?.Invoke();
            IsDone = result.Ok;
        }
        finally
        {
            IsRunning = false;
            _cts = null;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(IsDone);
}
