using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace CarsControl.App.Services;

/// <summary>
/// Last line of defense: an unexpected error in a button's action used to close the whole app without a
/// word. Now it's written to %LOCALAPPDATA%\CarsControl\logs\errors.log, you get a themed message, and the
/// app keeps running.
/// </summary>
public static class ErrorReporter
{
    public static string LogPath =>
        Path.Combine(Core.Util.UserData.Root, "logs", "errors.log");

    private static bool _showing;

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Unhandled (fatal)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("UI", e.Exception);
        e.Handled = true;
        if (_showing) return; // one message at a time, even if errors repeat
        _showing = true;
        _ = ShowAsync(e.Exception);
    }

    private static async Task ShowAsync(Exception ex)
    {
        try
        {
            var report = await Dialogs.ConfirmAsync(
                $"Something went wrong, but C.A.R.S. kept running.\n\n{ex.Message}\n\nDetails were saved to:\n{LogPath}\n\n" +
                "Report it opens a bug report in your browser with this error filled in (your user name, PC name and folder paths removed). You read it first; nothing is sent unless you submit it.",
                "Report it", "Close", "Something went wrong");
            if (report)
            {
                var helper = App.Services?.GetService(typeof(Core.Elevation.HelperClient)) as Core.Elevation.HelperClient;
                BugReport.Open(helper?.IsConnected ?? false, ex);
            }
        }
        catch
        {
            // The dialog itself failed (e.g. during shutdown): the log still has it.
        }
        finally
        {
            _showing = false;
        }
    }

    private const long MaxLogBytes = 1_000_000;
    private static readonly object LogGate = new();
    private static string? _lastKey;
    private static DateTime _lastAt;
    private static int _repeats;

    /// <summary>
    /// Appends to errors.log. The same error repeating (a layout or binding failure re-runs on every frame
    /// and once wrote 51 MB in an hour) is written once with a repeat count, and the file is rotated at 1 MB
    /// (errors.log.1 keeps the previous one), so it can never fill the disk.
    /// </summary>
    public static void Log(string where, Exception? ex)
    {
        try
        {
            lock (LogGate)
            {
                var key = where + "|" + ex?.GetType().Name + "|" + ex?.Message;
                if (key == _lastKey && DateTime.Now - _lastAt < TimeSpan.FromSeconds(60))
                {
                    _repeats++;
                    return;
                }
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                    File.Move(LogPath, LogPath + ".1", overwrite: true);
                var text = _repeats > 0 ? $"    (the previous error repeated {_repeats} more times)" + Environment.NewLine + Environment.NewLine : "";
                _repeats = 0;
                _lastKey = key;
                _lastAt = DateTime.Now;
                File.AppendAllText(LogPath, text + $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
