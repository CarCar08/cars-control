using System.Diagnostics;
using CarsControl.Core.Gaming;
using CarsControl.Core.Storage;

namespace CarsControl.App.Services;

/// <summary>
/// Turns Gaming mode on when a game starts: every 5 seconds it compares running programs with the install
/// folders of your Steam, Epic and other games (re-read every 15 minutes) plus your own list. Off again
/// 30 seconds after the last game closes, so a game restarting itself doesn't flip it off and on.
/// </summary>
public sealed class GameWatcher(GameMode mode, AppSettings settings, InventoryStore inventory)
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OffDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FolderRefresh = TimeSpan.FromMinutes(15);

    private readonly Dictionary<int, string?> _paths = new();
    private IReadOnlyList<GameFolder> _folders = [];
    private DateTime _foldersAt = DateTime.MinValue;
    private DateTime? _lastSeen;
    private CancellationTokenSource? _cts;

    public int KnownGames => _folders.Count;

    /// <summary>The program that turned Gaming mode on ("wallpaper64.exe"), for Settings' "Not a game" button.</summary>
    public string? DetectedProgram { get; private set; }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        BackgroundTasks.Run(Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(Poll);
            do
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    ErrorReporter.Log("game watcher", ex);
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }, ct), "game watcher loop");
    }

    public void Stop(bool resetMode = true)
    {
        _cts?.Cancel();
        _cts = null;
        if (resetMode) Platform.OnUiThread(() => mode.SetDetected(null));
    }

    private void Tick()
    {
        var gaming = settings.Store.Current.Gaming;
        if (!gaming.Automatic)
        {
            if (mode.DetectedGame is not null) Platform.OnUiThread(() => mode.SetDetected(null));
            return;
        }
        if (DateTime.Now - _foldersAt > FolderRefresh) RefreshFolders();

        var (game, program) = FindRunningGame(gaming.ExtraGames, gaming.IgnoredPrograms);
        if (game is not null)
        {
            _lastSeen = DateTime.Now;
            DetectedProgram = program;
            if (mode.DetectedGame != game) Platform.OnUiThread(() => mode.SetDetected(game));
        }
        else if (mode.DetectedGame is not null && (_lastSeen is null || DateTime.Now - _lastSeen > OffDelay))
        {
            Platform.OnUiThread(() => mode.SetDetected(null));
        }
    }

    /// <summary>Re-reads the game folders now (after a scan finds new games, or for tests).</summary>
    public void RefreshFolders()
    {
        try
        {
            var (games, _) = GameLibraries.Read(inventory.Current?.Apps);
            _folders = games.Where(g => !string.IsNullOrWhiteSpace(g.InstallPath))
                .Select(g => new GameFolder(g.Name, g.InstallPath!))
                .ToList();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("game folders", ex);
        }
        _foldersAt = DateTime.Now;
    }

    private (string? Game, string? Program) FindRunningGame(IReadOnlyCollection<string> extra, IReadOnlyCollection<string> ignored)
    {
        var processes = Process.GetProcesses();
        var alive = new HashSet<int>();
        string? found = null, program = null;
        foreach (var p in processes)
        {
            using (p)
            {
                if (p.Id <= 4 || p.SessionId == 0) continue; // system and services
                alive.Add(p.Id);
                if (found is not null) continue;
                if (!_paths.TryGetValue(p.Id, out var path)) _paths[p.Id] = path = GameDetector.ImagePath(p.Id);
                if (path is not null && GameDetector.Match(path, _folders, extra, ignored) is { } name)
                {
                    found = name;
                    program = System.IO.Path.GetFileName(path);
                }
            }
        }
        foreach (var gone in _paths.Keys.Where(k => !alive.Contains(k)).ToList()) _paths.Remove(gone);
        return (found, program);
    }
}
