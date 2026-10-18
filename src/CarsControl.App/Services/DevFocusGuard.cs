using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CarsControl.App.Services;

/// <summary>
/// Test copies only: if one of their windows becomes active anyway (WPF moving keyboard focus while a page loads can activate
/// the window, and Windows lets it take the foreground when the app that launched the tests allows that), the foreground goes
/// straight back to the window the user was in, and what caused it is written to perf.log with a stack trace.
/// </summary>
public static class DevFocusGuard
{
    private const int WM_ACTIVATE = 0x0006, WA_INACTIVE = 0;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0;

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private static WinEventProc? _proc; // kept alive: the hook calls it from native code
    private static IntPtr _lastOther;
    private static readonly int OwnPid = Environment.ProcessId;

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    /// <summary>You clicked the test copy yourself (left button down over one of its windows): let it have the foreground.</summary>
    private static bool ClickedByYou() =>
        GetAsyncKeyState(0x01) < 0 && GetCursorPos(out var p) && IsOwn(WindowFromPoint(p));
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    /// <summary>
    /// Test copies: marks the window "no-activate" before it's first shown (call from SourceInitialized), so neither its first
    /// appearance nor focus moved into it by UI Automation brings it in front of what you're doing.
    /// </summary>
    public static void NoActivate(Window window)
    {
        if (!App.IsDevInstance) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_NOACTIVATE));
    }

    public static void Install()
    {
        if (!App.IsDevInstance) return;
        Remember(GetForegroundWindow());
        _proc = (_, _, hwnd, _, _, _, _) => OnForeground(hwnd);
        SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Watch((Window)s)));
    }

    private static bool IsOwn(IntPtr hwnd) => hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid == OwnPid;

    /// <summary>Any window of this process in the foreground (popups and hidden helper windows too): log it and hand it back.</summary>
    private static void OnForeground(IntPtr hwnd)
    {
        if (!IsOwn(hwnd)) { Remember(hwnd); return; }
        if (ClickedByYou()) return;
        var cls = new System.Text.StringBuilder(256); GetClassName(hwnd, cls, cls.Capacity);
        var title = new System.Text.StringBuilder(256); GetWindowText(hwnd, title, title.Capacity);
        PerfLog.Mark($"FOREGROUND TAKEN by test copy window class '{cls}' title '{title}' visible {IsWindowVisible(hwnd)}; handing it back");
        var back = _lastOther;
        if (back != IntPtr.Zero && IsWindow(back)) SetForegroundWindow(back);
    }

    private static void Remember(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && !IsOwn(hwnd)) _lastOther = hwnd;
    }

    private static readonly HashSet<IntPtr> Watched = [];

    private static void Watch(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source || !Watched.Add(source.Handle)) return;
        NoActivate(window); // windows that didn't opt in at SourceInitialized

        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_ACTIVATE && (wParam.ToInt64() & 0xFFFF) != WA_INACTIVE && !ClickedByYou())
            {
                PerfLog.Mark($"ACTIVATED: test copy window '{window.Title}' (foreground: {(IsOwn(GetForegroundWindow()) ? "taken, handing it back" : "not taken")}). Caused by:{Environment.NewLine}{new StackTrace(1, false)}");
                var back = _lastOther;
                if (back != IntPtr.Zero)
                    window.Dispatcher.BeginInvoke(() => { if (IsWindow(back) && IsOwn(GetForegroundWindow())) SetForegroundWindow(back); });
            }
            return IntPtr.Zero;
        });
    }
}
