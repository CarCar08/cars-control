namespace CarsControl.App.Services;

/// <summary>
/// Gaming mode: on while a game runs (detected by <see cref="GameWatcher"/>) or when you switch it on.
/// While on, C.A.R.S. pauses its own background work (update checks, maintenance, approval toasts) and
/// <see cref="GamingEffects"/> pauses Windows Update and search indexing. Salad, notifications and your
/// power plan are never touched.
/// </summary>
public sealed class GameMode
{
    private string? _snoozed;

    public bool IsOn => IsManual || DetectedGame is not null;

    /// <summary>Switched on by you (tray, Home, palette); stays on until you switch it off.</summary>
    public bool IsManual { get; private set; }

    /// <summary>The game that's running, when detected automatically.</summary>
    public string? DetectedGame { get; private set; }

    public DateTime? Since { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="IsOn"/> or the game changes.</summary>
    public event Action? Changed;

    public string Describe() =>
        !IsOn ? "Off"
        : DetectedGame is { } g ? $"On — {g}"
        : "On (switched on by you)";

    /// <summary>Your switch. Switching off while a game runs snoozes detection for that game until it closes.</summary>
    public void SetManual(bool on)
    {
        var was = IsOn;
        IsManual = on;
        if (!on && DetectedGame is { } game)
        {
            _snoozed = game;
            DetectedGame = null;
        }
        if (on) _snoozed = null;
        Update(was);
    }

    /// <summary>From the watcher: the running game, or null when none is.</summary>
    public void SetDetected(string? game)
    {
        if (game is null) _snoozed = null;          // the snoozed game closed
        else if (game == _snoozed) return;           // you switched Gaming mode off for this one
        if (DetectedGame == game) return;
        var was = IsOn;
        DetectedGame = game;
        Update(was);
    }

    /// <summary>Callers are on the UI thread (the watcher marshals its results there).</summary>
    private void Update(bool was)
    {
        if (IsOn && !was) Since = DateTime.Now;
        if (!IsOn) Since = null;
        Changed?.Invoke();
    }
}
