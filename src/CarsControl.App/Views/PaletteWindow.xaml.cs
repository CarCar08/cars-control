using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CarsControl.App.Services;
using CarsControl.App.Services.Palette;
using CarsControl.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;

namespace CarsControl.App.Views;

/// <summary>
/// The command palette window: borderless, on top, centred near the top of the screen you're working on
/// (the one with the mouse when opened by the global shortcut). It closes when it loses focus.
/// </summary>
public partial class PaletteWindow : Window
{
    private static PaletteWindow? _open;
    private readonly PaletteViewModel _vm;
    private readonly bool _fromHotkey;
    private bool _closing;

    private PaletteWindow(PaletteViewModel vm, bool fromHotkey)
    {
        InitializeComponent();
        _vm = vm;
        _fromHotkey = fromHotkey;
        DataContext = vm;
        vm.CloseRequested += SafeClose;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaletteViewModel.Selected) && vm.Selected is { } row) Results.ScrollIntoView(row);
        };
        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => { Services.DevFocusGuard.NoActivate(this); Place(); };
        Loaded += (_, _) =>
        {
            if (App.IsDevInstance) return; // test copies never take the keyboard
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        };
        // Clicking anywhere else closes it, like Start search. Test copies never take focus, so they stay.
        Deactivated += (_, _) =>
        {
            if (!App.IsDevInstance) SafeClose();
        };
        Closed += (_, _) => _open = null;
    }

    /// <summary>Opens the palette, or brings the open one back.</summary>
    public static void Open(bool fromHotkey = false)
    {
        if (_open is { } existing)
        {
            if (App.IsDevInstance) return;
            existing.Activate();
            existing.SearchBox.Focus();
            return;
        }
        var vm = new PaletteViewModel(App.Services.GetRequiredService<PaletteService>());
        var w = new PaletteWindow(vm, fromHotkey);
        if (App.IsDevInstance) w.ShowActivated = false;
        _open = w;
        w.Show();
        if (!App.IsDevInstance) w.Activate();
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private async void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _vm.Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                _vm.Move(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                _vm.Move(6);
                e.Handled = true;
                break;
            case Key.PageUp:
                _vm.Move(-6);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                await _vm.RunAsync();
                break;
            case Key.Right when SearchBox.CaretIndex >= SearchBox.Text.Length:
                if (_vm.ShowMore()) e.Handled = true;
                break;
            case Key.Left when _vm.MoreFor is not null && SearchBox.Text.Length == 0:
            case Key.Back when _vm.MoreFor is not null && SearchBox.Text.Length == 0:
                _vm.Back();
                e.Handled = true;
                break;
            case Key.Escape:
                if (!_vm.Back()) SafeClose();
                e.Handled = true;
                break;
        }
    }

    private async void OnResultClicked(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PaletteRow { IsHeader: false } row) await _vm.RunAsync(row);
    }

    // ---------- placement and frame ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point pt);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x1, SwpNoZOrder = 0x4, SwpNoActivate = 0x10;

    /// <summary>Near the top of the screen you're using: the mouse's for the global shortcut, the main
    /// window's otherwise (test copies: always the main window's, which is on the side monitor).</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));

        var main = Application.Current.MainWindow;
        IntPtr monitor;
        if ((_fromHotkey && !App.IsDevInstance) || main is not { IsVisible: true })
        {
            GetCursorPos(out var cursor);
            monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        }
        else
        {
            monitor = MonitorFromWindow(new WindowInteropHelper(main).Handle, MonitorDefaultToNearest);
        }
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(hwnd, out var r)) return;
        var width = r.Right - r.Left;
        var work = info.Work;
        var x = work.Left + (work.Right - work.Left - width) / 2;
        var y = work.Top + (work.Bottom - work.Top) * 16 / 100;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }
}
