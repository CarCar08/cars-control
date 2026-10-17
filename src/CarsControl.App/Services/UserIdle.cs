using System.Runtime.InteropServices;

namespace CarsControl.App.Services;

/// <summary>Whether you're at the PC: time since the last keyboard or mouse input, and whether a
/// full-screen app (game, video, presentation) has the screen. Scheduled work waits for both.</summary>
public static class UserIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    // QUERY_USER_NOTIFICATION_STATE: 2 busy (full-screen app), 3 Direct3D full screen, 4 presentation mode.
    private const int QunsBusy = 2, QunsD3dFullScreen = 3, QunsPresentationMode = 4;

    /// <summary>Time since the last keyboard or mouse input (game controllers don't count).</summary>
    public static TimeSpan Current
    {
        get
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
        }
    }

    /// <summary>A full-screen app or presentation is in front (watching a video counts).</summary>
    public static bool FullScreenBusy =>
        SHQueryUserNotificationState(out var s) == 0 && s is QunsBusy or QunsD3dFullScreen or QunsPresentationMode;
}
