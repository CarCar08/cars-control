using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

// Shared with the installer (linked into CarsControl.Setup, which targets .NET Framework 4.8): keep it free of
// anything only the app has.
namespace CarsControl.App.Services;

/// <summary>Test copies (CARS_DEV_INSTANCE=1) open on a monitor that isn't being used and never take focus,
/// so automated UI checks don't pop up over a game or a video.
/// <para>CARS_DEV_MONITOR picks which: <c>tv</c> (default: the other monitor that does NOT touch the primary,
/// e.g. the 4K TV), <c>adjacent</c> (the secondary monitor that touches the primary), or a device name such
/// as <c>\\.\DISPLAY3</c>. When the wanted one doesn't exist, the other secondary monitor is used; with
/// a single monitor the window stays where Windows puts it.</para></summary>
internal static class DevWindowPlacement
{
    private const uint MONITORINFOF_PRIMARY = 1;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    private static List<MonitorInfoEx> Monitors()
    {
        var list = new List<MonitorInfoEx>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info)) list.Add(info);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static bool Touches(Rect a, Rect b)
    {
        static bool Overlap(int a1, int a2, int b1, int b2) => a1 < b2 && b1 < a2;
        return ((a.Right == b.Left || a.Left == b.Right) && Overlap(a.Top, a.Bottom, b.Top, b.Bottom))
               || ((a.Bottom == b.Top || a.Top == b.Bottom) && Overlap(a.Left, a.Right, b.Left, b.Right));
    }

    /// <summary>The work area to use for test windows, or null on a single-monitor PC.</summary>
    private static Rect? Target()
    {
        var monitors = Monitors();
        var primary = monitors.FirstOrDefault(m => (m.Flags & MONITORINFOF_PRIMARY) != 0);
        var others = monitors.Where(m => (m.Flags & MONITORINFOF_PRIMARY) == 0).ToList();
        if (primary.Size == 0 || others.Count == 0) return null;

        var wanted = (Environment.GetEnvironmentVariable("CARS_DEV_MONITOR") ?? "tv").Trim();
        var adjacent = others.Where(m => Touches(m.Monitor, primary.Monitor)).ToList();
        var far = others.Where(m => !Touches(m.Monitor, primary.Monitor)).ToList();

        MonitorInfoEx pick;
        if (wanted.StartsWith(@"\\.\", StringComparison.Ordinal) && others.FirstOrDefault(m => m.Device == wanted) is { Size: > 0 } named) pick = named;
        else if (wanted.Equals("adjacent", StringComparison.OrdinalIgnoreCase)) pick = adjacent.FirstOrDefault(m => m.Size > 0).Size > 0 ? adjacent[0] : others[0];
        else pick = far.Count > 0 ? far[0] : adjacent.Count > 0 ? adjacent[0] : others[0]; // "tv": the one that doesn't touch the primary
        return pick.Work;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>Where to create the window so it's on the test monitor from its very first frame, in WPF's
    /// device-independent units (WPF-UI raises Loaded before SourceInitialized, so moving it afterwards left it
    /// visible on the primary monitor for a moment: over a game). Null on a single-monitor PC.</summary>
    public static Point? InitialPosition()
    {
        if (Target() is not { } r) return null;
        var scale = GetDpiForSystem() / 96.0;
        return new Point((r.Left + 40) / scale, (r.Top + 40) / scale);
    }

    /// <summary>Call from SourceInitialized (before the window is first shown).</summary>
    public static void MoveToSecondaryMonitor(Window window)
    {
        if (Target() is not { } r) return; // single monitor: leave it where Windows puts it
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowPos(hwnd, IntPtr.Zero, r.Left + 40, r.Top + 40, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Call once the window has its real size on the new monitor (Loaded): a TV scaled to 1536x864
    /// is shorter than the default window, so it's shrunk to fit the work area (physical pixels).</summary>
    public static void FitToMonitor(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var work = info.Work;
        var width = Math.Min(rect.Width, work.Width - 40);
        var height = Math.Min(rect.Height, work.Height - 40);
        var x = Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - width));
        var y = Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
        if (width != rect.Width || height != rect.Height || x != rect.Left || y != rect.Top)
            SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>Shows or restores the window without activating it or raising it above other windows.</summary>
    public static void ShowWithoutFocus(Window window)
    {
        if (!window.IsVisible) window.Show();
        ShowWindow(new WindowInteropHelper(window).Handle, SW_SHOWNOACTIVATE);
    }
}
