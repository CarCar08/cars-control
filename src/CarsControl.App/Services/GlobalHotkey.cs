using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CarsControl.Core.Settings;

namespace CarsControl.App.Services;

/// <summary>
/// The command palette's system-wide shortcut (default Ctrl+Alt+Space), registered on the main window,
/// which exists even while the app sits in the tray. If another app already owns the combination,
/// Windows refuses it and Settings says so.
/// </summary>
public sealed class GlobalHotkey
{
    private const int HotkeyId = 0xCA25;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private HwndSource? _source;
    private bool _registered;

    /// <summary>"Ctrl+Alt+Space is used by another app — pick another one" or null.</summary>
    public string? Problem { get; private set; }

    public bool IsRegistered => _registered;

    public event Action? Pressed;
    public event Action? Changed;

    public void Attach(Window window, PaletteSettings settings)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        Apply(settings);
    }

    /// <summary>(Re-)registers from settings. Test copies don't take the shortcut from the real app
    /// unless CARS_DEV_HOTKEY=1.</summary>
    public void Apply(PaletteSettings settings)
    {
        if (_source is null) return;
        if (_registered) UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
        Problem = null;
        if (settings.GlobalHotkey && !(App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEV_HOTKEY") != "1"))
        {
            if (!TryParse(settings.Hotkey, out var mods, out var vk))
                Problem = $"\"{settings.Hotkey}\" isn't a shortcut Windows can register (it needs Ctrl, Alt or Win plus a key).";
            else if (!RegisterHotKey(_source.Handle, HotkeyId, mods | ModNoRepeat, vk))
                Problem = $"{settings.Hotkey} is already used by another app. Pick another one.";
            else
                _registered = true;
        }
        Changed?.Invoke();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    /// <summary>"Ctrl+Alt+Space" → modifier flags and a virtual-key code.</summary>
    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        Key? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win" or "windows": modifiers |= ModWin; break;
                default:
                    try
                    {
                        if (key is not null || new KeyConverter().ConvertFromInvariantString(part) is not Key k) return false;
                        key = k;
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or FormatException)
                    {
                        return false;
                    }
                    break;
            }
        }
        // A shortcut without Ctrl, Alt or Win would take a plain key (or Shift+key) away from every app.
        if (key is null || (modifiers & (ModControl | ModAlt | ModWin)) == 0) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return vk != 0;
    }

    /// <summary>The text for a key press in the shortcut recorder ("Ctrl+Alt+Space"), or null while only
    /// modifiers are held.</summary>
    public static string? Describe(ModifierKeys modifiers, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(new KeyConverter().ConvertToInvariantString(key) ?? key.ToString());
        return string.Join("+", parts);
    }
}
