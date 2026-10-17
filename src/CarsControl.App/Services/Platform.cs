using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace CarsControl.App.Services;

/// <summary>
/// The few things view models need from Windows and the UI toolkit, in one place (with
/// <see cref="Dialogs"/> and <see cref="Navigator"/>). View models call these instead of WPF or
/// Explorer directly, so a future macOS UI only needs its own version of these classes.
/// </summary>
public static class Platform
{
    /// <summary>Runs on the UI thread (directly when there's no UI, e.g. in tests). Background priority
    /// lets pending input finish first, which coalesces bursts of changes into one update.</summary>
    public static void OnUiThread(Action action, bool background = false)
    {
        if (App.IsShuttingDown || Application.Current?.Dispatcher.HasShutdownStarted == true) return;
        if (Application.Current?.Dispatcher is not { } d) action();
        else d.BeginInvoke(background ? DispatcherPriority.Background : DispatcherPriority.Normal, action);
    }

    public static void CopyText(string? text)
    {
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    /// <summary>Opens a folder, or shows a file selected in its folder.</summary>
    public static void ShowInFileManager(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }

    /// <summary>The standard "Save as" dialog; null when cancelled. Filter like "CSV file (*.csv)|*.csv".</summary>
    public static string? PickSaveFile(string title, string defaultName, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, FileName = defaultName, Filter = filter, OverwritePrompt = true };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    /// <summary>The standard folder picker; null when cancelled.</summary>
    public static string? PickFolder(string title, string? initial = null)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, InitialDirectory = initial is not null && Directory.Exists(initial) ? initial : "" };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FolderName : null;
    }

    /// <summary>The standard "Open" dialog; null when cancelled.</summary>
    public static string? PickOpenFile(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    /// <summary>Shows the move window over the main window; true when something moved.</summary>
    public static bool ShowMove(ViewModels.MoveViewModel vm) => Views.MoveWindow.Open(vm);

    /// <summary>Hands a link to its app (steam://…, https://…).</summary>
    public static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    /// <summary>
    /// ShowDialog, except in a test copy: a modal window always activates itself (taking the keyboard from whatever you're typing
    /// in), so there it's shown without activation and waited for in a nested message loop instead.
    /// </summary>
    public static void ShowModal(Window window)
    {
        if (!App.IsDevInstance) { window.ShowDialog(); return; }
        window.ShowActivated = false;
        window.SourceInitialized += (_, _) => DevFocusGuard.NoActivate(window);
        var frame = new DispatcherFrame();
        window.Closed += (_, _) => frame.Continue = false;
        window.Show();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Shows the uninstall window over the main window; true when something changed.</summary>
    public static bool ShowUninstall(ViewModels.UninstallViewModel vm)
    {
        var window = new Views.UninstallWindow(vm) { Owner = Application.Current.MainWindow };
        ShowModal(window);
        return window.Changed;
    }
}
