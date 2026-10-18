using System.Collections.ObjectModel;
using System.Text.Json;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Optimize;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public enum FrameTestStep { Pick, Recording, NeedsRestart, Result }

/// <summary>A test waiting for a restart: the change is applied and the first minute is saved.</summary>
public sealed record PendingFrameTest(string TweakId, string GameFile, bool OriginalOn, FrameSummary Before);

/// <summary>
/// "Test it on this PC": a minute of the game before, the change, a minute after, and a verdict that only says "better" when the
/// difference beats the game's own variation. Changes that need a restart pause in between and continue from the Optimization page.
/// </summary>
public sealed partial class FrameTestViewModel : ObservableObject
{
    public const string PendingKey = "frame_test_pending";
    public const int Seconds = 60;
    private const int Countdown = 10;

    private readonly TweakRowViewModel _row;
    private readonly ITweakRowOwner _owner;
    private readonly HelperClient _helper;
    private readonly GameWatcher _games;
    private readonly CarsDb _db;
    private readonly bool _originalOn;
    private FrameSummary? _before;
    private CancellationTokenSource? _cts;
    private bool _stopped; // Stop or Close pressed: whatever the running capture returns is thrown away, nothing is changed

    public FrameTestViewModel(TweakRowViewModel row, ITweakRowOwner owner, HelperClient helper, GameWatcher games, CarsDb db, PendingFrameTest? pending = null)
    {
        _row = row;
        _owner = owner;
        _helper = helper;
        _games = games;
        _db = db;
        _originalOn = pending?.OriginalOn ?? row.IsOn;
        _before = pending?.Before;
        BackgroundTasks.Run(RefreshProgramsAsync(pending?.GameFile), "frame test programs");
        Intro = pending is null
            ? $"Start your game and get to a spot you can repeat (a benchmark, the same map or area). C.A.R.S. records a minute, {ChangeText(!_originalOn)}, then records another minute and compares."
            : $"The change is applied and the first minute is saved ({pending.Before.Describe()}). Start the same game, go to the same spot, then record the second minute.";
    }

    public string Title => "Test it on this PC: " + _row.Title;
    public string Intro { get; }
    public ObservableCollection<GameWatcher.RunningProgram> Programs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordCommand))]
    private GameWatcher.RunningProgram? _selectedProgram;
    [ObservableProperty] private FrameTestStep _step = FrameTestStep.Pick;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _pickHint;

    public bool HasBaseline => _before is not null;
    public string RecordButton => HasBaseline ? "Record the second minute" : "Record the first minute";
    public string KeepText => _originalOn ? "Keep it off" : "Keep it on";
    public string UndoText => _originalOn ? "Turn it back on" : "Turn it back off";

    public event Action? CloseRequested;

    private string ChangeText(bool on) => (on ? "turns on " : "turns off ") + $"\"{_row.Title}\"";

    [RelayCommand]
    private async Task RefreshProgramsAsync(string? preferFile = null)
    {
        var keep = preferFile ?? SelectedProgram?.FileName;
        // Off the UI thread: it looks at every process and may re-read the game libraries from disk.
        var running = await Task.Run(_games.RunningPrograms);
        Programs.Clear();
        foreach (var p in running) Programs.Add(p);
        SelectedProgram = Programs.FirstOrDefault(p => keep is not null && p.FileName.Equals(keep, StringComparison.OrdinalIgnoreCase))
            ?? Programs.FirstOrDefault(p => p.IsGame);
        PickHint = SelectedProgram is not null ? null
            : Programs.Any() ? "No game was recognized. Start your game and press Refresh, or pick its window from the list."
            : "Nothing to test yet: start your game, then press Refresh.";
    }

    private bool CanRecord() => SelectedProgram is not null && !IsBusy;

    partial void OnSelectedProgramChanged(GameWatcher.RunningProgram? value)
    {
        if (value is not null) PickHint = null;
    }

    [RelayCommand(CanExecute = nameof(CanRecord))]
    private async Task RecordAsync()
    {
        if (SelectedProgram is not { } game || IsBusy) return;
        IsBusy = true;
        Error = null;
        _stopped = false;
        _cts = new CancellationTokenSource();
        try
        {
            // Ask for admin now, while you're still here: a prompt popping up after you've switched to the game would be useless.
            Status = "Getting admin approval (Windows only lets administrators read frame times)…";
            try
            {
                await _helper.SendAsync(new PingOp(), dryRun: HelperClient.DevUnelevated);
            }
            catch (HelperCancelledException)
            {
                Error = "Recording needs admin approval; it was cancelled, so nothing was recorded.";
                return;
            }
            catch (HelperUnavailableException ex)
            {
                Error = ex.Message;
                return;
            }

            Step = FrameTestStep.Recording;
            for (var s = Countdown; s > 0; s--)
            {
                Status = $"Switch to {game.Name} now. Recording starts in {s} s…";
                Progress = 0;
                await Task.Delay(1000, _cts.Token);
            }
            Status = $"Recording {game.Name}: keep playing normally for {Seconds} seconds.";
            var ticker = Tick(_cts.Token);
            var summary = await CaptureAsync(game);
            _cts.Cancel();
            await ticker;
            if (_stopped)
            {
                // The helper's recording can't be interrupted; it finished after you stopped, so its result is discarded.
                Status = "Stopped. Nothing was changed.";
                Step = FrameTestStep.Pick;
                return;
            }
            if (summary is null)
            {
                Status = "";
                Step = FrameTestStep.Pick;
                return;
            }

            if (_before is null)
            {
                _before = summary;
                OnPropertyChanged(nameof(HasBaseline));
                OnPropertyChanged(nameof(RecordButton));
                await _owner.ChangeAsync(_row, !_originalOn);
                if (_row.IsOn == _originalOn)
                {
                    Error = "The change couldn't be applied, so there's nothing to compare: " + (_row.Status ?? "Windows refused it.");
                    Step = FrameTestStep.Pick;
                    return;
                }
                if (_row.Tweak.TakesEffect is { } when)
                {
                    _db.SetSetting(PendingKey, JsonSerializer.Serialize(new PendingFrameTest(_row.Tweak.Id, game.FileName, _originalOn, summary)));
                    Status = $"First minute: {summary.Describe()}. The change takes effect {when}. When it has, start the same game, go to the same spot, then open C.A.R.S. › Optimization and press Continue the test.";
                    Step = FrameTestStep.NeedsRestart;
                    return;
                }
                Status = $"First minute: {summary.Describe()}. The change is applied; go back to the same spot and record the second minute.";
                Step = FrameTestStep.Pick;
                return;
            }

            _db.SetSetting(PendingKey, "");
            var comparison = FrameStats.Compare(_before, summary);
            ResultText = comparison.Text + Environment.NewLine + Environment.NewLine + comparison.Verdict switch
            {
                FrameVerdict.Better => $"{KeepText} — it measurably helps this game here.",
                FrameVerdict.Worse => $"{UndoText} — it measurably hurts this game here.",
                _ => $"Without a measurable gain, the safe choice is to {UndoText.ToLowerInvariant()}.",
            };
            _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "optimize", "frametest:" + _row.Tweak.Id, _row.Title, "Optimization", game.Name, null,
                false, "Succeeded", 0, comparison.Verdict.ToString(), comparison.Text));
            Step = FrameTestStep.Result;
        }
        catch (OperationCanceledException)
        {
            Step = FrameTestStep.Pick;
            Status = "Stopped.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<FrameSummary?> CaptureAsync(GameWatcher.RunningProgram game)
    {
        try
        {
            var r = await _helper.SendAsync(new FrameCaptureOp(game.ProcessId, Seconds), dryRun: HelperClient.DevUnelevated);
            if (r.Status == OpStatus.DryRun)
            {
                Error = "Test copies can't record (no admin helper): " + r.Message;
                return null;
            }
            if (!r.Ok || r.Data is null)
            {
                Error = r.Message ?? "Nothing was recorded.";
                return null;
            }
            return HelperJson.Deserialize<FrameSummary>(r.Data);
        }
        catch (HelperCancelledException)
        {
            Error = "Recording needs admin approval (Windows only lets administrators read frame times); it was cancelled.";
            return null;
        }
        catch (HelperUnavailableException ex)
        {
            Error = ex.Message;
            return null;
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        var start = DateTime.Now;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Progress = Math.Min(100, (DateTime.Now - start).TotalSeconds / Seconds * 100);
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // done
        }
    }

    [RelayCommand]
    private void Keep() => CloseRequested?.Invoke();

    [RelayCommand]
    private async Task UndoAsync()
    {
        await _owner.ChangeAsync(_row, _originalOn);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Stop()
    {
        _stopped = true;
        _cts?.Cancel();
        if (Step == FrameTestStep.Recording) Status = "Stopping: the recording ends by itself within a minute and is thrown away. Nothing will be changed.";
    }

    [RelayCommand]
    private void Close()
    {
        _stopped = true;
        _cts?.Cancel();
        CloseRequested?.Invoke();
    }

    public static PendingFrameTest? ReadPending(CarsDb db)
    {
        try
        {
            return db.GetSetting(PendingKey) is { Length: > 0 } json ? JsonSerializer.Deserialize<PendingFrameTest>(json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
