using System.Windows;
using System.Windows.Media;
using CarsControl.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace CarsControl.App.Services;

/// <summary>
/// Applies Settings › Appearance: theme (dark / light / follow Windows), background (translucent Mica or
/// solid) and accent (C.A.R.S. teal, your Windows accent, or a custom color). The default is translucent
/// dark with C.A.R.S. teal: with a pink-red Windows accent, "info" banners and buttons looked like errors.
/// </summary>
/// <remarks>
/// The windows always keep their Mica backdrop. "Solid" is an opaque background drawn over it: changing
/// <see cref="FluentWindow.WindowBackdropType"/> on a window that already exists makes WPF-UI 4.3 rebuild
/// the window chrome and throw ("The provided DependencyObject is not a context for this Freezable").
/// </remarks>
public static class AppearanceService
{
    private static AppearanceSettings _current = new();
    private static ApplicationTheme _theme = ApplicationTheme.Dark;
    private static bool _hooked;
    private static bool _refreshQueued;
    private static Color _systemColor;
    private static readonly List<WeakReference<FluentWindow>> Windows = [];

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>Call once at startup before any window is shown, and again whenever the settings change.</summary>
    public static void Apply(AppearanceSettings settings)
    {
        if (!_hooked)
        {
            // "Same as Windows": the watcher re-applies the theme when Windows switches; repaint the
            // windows so their dark/light Mica follows.
            _hooked = true;
            ApplicationThemeManager.Changed += (theme, _) =>
            {
                _theme = theme;
                foreach (var w in LiveWindows()) { Paint(w); PaintAfterTheme(w); }
            };
        }
        _current = settings;

        var theme = settings.Theme switch
        {
            ThemeChoice.Light => ApplicationTheme.Light,
            ThemeChoice.System => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
            _ => ApplicationTheme.Dark,
        };
        _theme = theme;
        _systemColor = ApplicationAccentColorManager.GetColorizationColor();
        // Accent first: reloading the theme copies the accent that's current at that moment into the
        // controls' brushes, so applying it only afterwards made every switch show the previous choice
        // ("Windows accent" stayed teal, then "teal" turned red). Apply it again after, for the app-level keys.
        ApplyAccent(settings, theme);
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);
        ApplyAccent(settings, theme);

        foreach (var w in LiveWindows())
        {
            Paint(w);
        }
    }

    /// <summary>Call for every window when it's created (main window, uninstall window…).</summary>
    public static void Track(FluentWindow window)
    {
        Windows.Add(new WeakReference<FluentWindow>(window));
        window.SourceInitialized += (_, _) =>
        {
            Paint(window);
            var source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(window).Handle);
            source?.AddHook(SystemThemeMessage);
            window.Closed += (_, _) => source?.RemoveHook(SystemThemeMessage);
        };
        window.Loaded += (_, _) =>
        {
            Paint(window);
            PaintAfterTheme(window);
            // Checked once the window is idle: WPF-UI sets the DWM backdrop in its own Loaded handler, which may run after
            // this one, so checking here directly logged a false "MISMATCH, dwm=0".
            window.Dispatcher.BeginInvoke(() => Verify(window, $"{window.GetType().Name} loaded"), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            // Again a little later: WPF-UI's own theme and backdrop code runs after Loaded and has repainted windows before.
            var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            later.Tick += (_, _) => { later.Stop(); Paint(window); Verify(window, $"{window.GetType().Name} +3 s"); };
            later.Start();
        };
    }

    private static void PaintAfterTheme(FluentWindow window)
    {
        if (!window.Dispatcher.HasShutdownStarted)
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () => Paint(window));
    }

    private static void Paint(FluentWindow w)
    {
        if (_current.Backdrop == BackdropChoice.Solid) w.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ApplicationBackgroundBrush");
        else w.Background = Brushes.Transparent; // lets Mica show through

        // Windows draws Mica (and the title bar) dark or light per window, following the *system* setting
        // unless told otherwise. Light theme on dark Windows gave dark Mica under dark text: unreadable.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return; // not created yet; SourceInitialized paints again
        var dark = _theme == ApplicationTheme.Dark ? 1 : 0;
        // Reapplying an unchanged DWM preference can notify the system-theme watcher again.
        // Do not let its Changed callback form a repaint/notification loop.
        if (DwmGetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, out var previous, sizeof(int)) != 0 || previous != dark)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38; // 1 none, 2 Mica, 3 Acrylic, 4 Mica Alt

    /// <summary>
    /// What the window really has: the backdrop Windows draws (DWM) and whether our background lets it show through.
    /// Logged to perf.log; a translucent choice with an opaque background or no DWM backdrop is logged as a mismatch.
    /// (Mica shows a solid fallback color while a window is inactive: that is Windows, not a mismatch.)
    /// </summary>
    public static string Describe(FluentWindow w)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        var dwm = -1;
        if (hwnd != IntPtr.Zero) DwmGetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, out dwm, sizeof(int));
        var bg = w.Background is SolidColorBrush b ? b.Color.ToString() : w.Background?.GetType().Name ?? "none";
        var ok = _current.Backdrop == BackdropChoice.Solid || (dwm is 2 or 3 or 4 && w.Background is SolidColorBrush { Color.A: 0 });
        return $"backdrop {(ok ? "ok" : "MISMATCH")}: choice={_current.Backdrop}/{_current.Theme}, dwm={dwm}, background={bg}";
    }

    private static void Verify(FluentWindow w, string when) => PerfLog.Mark($"{when}: {Describe(w)}");

    private static IntPtr SystemThemeMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WPF-UI's watcher reapplies the entire theme for every color notification. With a tray icon
        // and an accessibility client this repeatedly rebuilds layout/automation peers, even when
        // the actual theme is unchanged. Coalesce notifications and compare the values first.
        var colorMessage = message is 0x320 or 0x31A or 0x15
            || (message == 0x1A && lParam != IntPtr.Zero && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet");
        if (!colorMessage || _refreshQueued || App.IsShuttingDown || (_current.Theme != ThemeChoice.System && _current.Accent != AccentChoice.Windows)) return IntPtr.Zero;
        _refreshQueued = true;
        Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            if (App.IsShuttingDown) return;
            var theme = ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
            var color = ApplicationAccentColorManager.GetColorizationColor();
            if ((_current.Theme == ThemeChoice.System && theme != _theme) || (_current.Accent == AccentChoice.Windows && color != _systemColor))
                Apply(_current);
        });
        return IntPtr.Zero;
    }

    private static void ApplyAccent(AppearanceSettings s, ApplicationTheme theme)
    {
        if (s.Accent == AccentChoice.Windows)
        {
            ApplicationAccentColorManager.ApplySystemAccent();
            return;
        }
        var hex = s.Accent == AccentChoice.Custom ? s.CustomAccent : Defaults.CarsTeal;
        if (!TryParse(hex, out var color)) TryParse(Defaults.CarsTeal, out color);
        ApplicationAccentColorManager.Apply(color, theme);
    }

    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        try
        {
            if (hex is null || !System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$")) return false;
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static IEnumerable<FluentWindow> LiveWindows()
    {
        Windows.RemoveAll(r => !r.TryGetTarget(out _));
        foreach (var r in Windows)
            if (r.TryGetTarget(out var w)) yield return w;
    }
}
