using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using CarsControl.App.Services;
using CarsControl.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace CarsControl.App;

public partial class MainWindow : FluentWindow
{
    private bool _exiting;
    private bool _startHidden;
    private readonly Stopwatch _navTimer = new();
    private readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromMinutes(3) };
    // While the window is in use: a minute after the last page switch, pages beyond the few most recent are released too.
    private readonly DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    internal const int PagesKeptWhileOpen = 4; // the current page and the three before it
    private Type? _currentPage;

    public MainWindow()
    {
        InitializeComponent();
        AppearanceService.Track(this);

        // The dev/test copy looks identical otherwise; on 2026-10-12 an "Update everything" clicked in
        // a test copy was rejected because its helper can't elevate.
        if (App.IsDevInstance)
        {
            // CARS_DOC_SHOT=1 (Capture-All -Docs) leaves the marker off, so the screenshots in docs/images look like the real app.
            var marker = Environment.GetEnvironmentVariable("CARS_DOC_SHOT") == "1" ? "" : "  ·  TEST COPY — can't do admin work";
            Title += marker;
            AppTitleBar.Title += marker;
            // Automated checks run while you use the PC: secondary monitor, never steals focus.
            ShowActivated = false;
            // Created on the test monitor from the first frame, and invisible until it's been fitted to it:
            // the window must never show up on the monitor you're playing or watching something on.
            if (DevWindowPlacement.InitialPosition() is { } where)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = where.X;
                Top = where.Y;
                Opacity = 0;
            }
            SourceInitialized += (_, _) => { Services.DevFocusGuard.NoActivate(this); DevWindowPlacement.MoveToSecondaryMonitor(this); };
            Loaded += (_, _) =>
            {
                DevWindowPlacement.FitToMonitor(this); // a TV scaled to 1536x864 is shorter than the default window
                Opacity = 1;
            };
        }

        // Pages come from PageCache: built once, then reused while the window is in use, so switching pages is instant and keeps
        // scroll position, selections and the open tab. After a few minutes in the tray every page but the current one is
        // released (their visual trees were most of the memory C.A.R.S. kept all day); they're rebuilt on the next visit.
        RootNavigation.SetServiceProvider(App.Services.GetRequiredService<PageCache>());
        _releaseTimer.Tick += (_, _) => { _releaseTimer.Stop(); ReleaseHiddenPages(); };
        _trimTimer.Tick += (_, _) => { _trimTimer.Stop(); if (IsVisible) TrimPages(); };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _releaseTimer.Stop();
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default; // back to the GPU
            }
            else _releaseTimer.Start();
        };
        RootNavigation.Navigating += (_, _) => _navTimer.Restart();
        RootNavigation.Navigated += (_, args) =>
        {
            _currentPage = args.Page?.GetType() ?? _currentPage;
            _trimTimer.Stop(); _trimTimer.Start();
            var name = args.Page?.GetType().Name ?? "?";
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => PerfLog.Duration($"navigate → {name}", _navTimer.Elapsed));
        };

        // Narrow windows get the icons-only sidebar (like Windows Settings): at the minimum size the full
        // one took a quarter of the width and left Storage's file list two rows tall. Only crossing the
        // threshold changes it, so opening or closing it yourself sticks until you resize past it again.
        SizeChanged += (_, e) =>
        {
            var wide = e.NewSize.Width >= WideSidebarWidth;
            if (e.PreviousSize.Width == 0 || (e.PreviousSize.Width >= WideSidebarWidth) != wide) RootNavigation.IsPaneOpen = wide;
        };

        Loaded += (_, _) =>
        {
            TidySidebarSeparators();
            RootNavigation.Navigate(StartPage ?? typeof(HomePage));
            if (StartHidden)
            {
                // Started with --background: the window had to be shown once so the tray icon registers.
                Hide();
                WindowState = WindowState.Normal;
                ShowInTaskbar = true;
            }
        };
        ContentRendered += (_, _) =>
        {
            PerfLog.Mark("first paint");
            if (DevVisualDump.Enabled) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => DevVisualDump.Write(this));
        };

        // Ctrl+1…9 and Ctrl+0 jump between pages in sidebar order, Ctrl+, opens Settings (the sidebar items expose no invoke action
        // to keyboard/automation users).
        Type[] order = [typeof(HomePage), typeof(AppsPage), typeof(RefreshPage), typeof(StoragePage), typeof(CleanupPage), typeof(HealthPage), typeof(PrivacyPage), typeof(OptimizePage), typeof(BackupPage), typeof(HistoryPage)];
        InputBindings.Add(new System.Windows.Input.KeyBinding(new CommunityToolkit.Mvvm.Input.RelayCommand(() => NavigateTo(typeof(SettingsPage))), System.Windows.Input.Key.OemComma, System.Windows.Input.ModifierKeys.Control));
        for (var n = 0; n < order.Length; n++)
        {
            var page = order[n];
            var command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => NavigateTo(page));
            InputBindings.Add(new System.Windows.Input.KeyBinding(command, n < 9 ? System.Windows.Input.Key.D1 + n : System.Windows.Input.Key.D0, System.Windows.Input.ModifierKeys.Control));
        }

        // Ctrl+K: the command palette. The global shortcut (Settings › Command palette) works from anywhere.
        InputBindings.Add(new System.Windows.Input.KeyBinding(
            new CommunityToolkit.Mvvm.Input.RelayCommand(() => PaletteWindow.Open()), System.Windows.Input.Key.K, System.Windows.Input.ModifierKeys.Control));
        var hotkey = App.Services.GetRequiredService<GlobalHotkey>();
        hotkey.Pressed += () => PaletteWindow.Open(fromHotkey: true);
        SourceInitialized += (_, _) => hotkey.Attach(this, App.Services.GetRequiredService<AppSettings>().Store.Current.Palette);

        var updates = App.Services.GetRequiredService<UpdateCenter>();
        updates.Changed += () =>
        {
            var n = updates.Found.Count;
            TrayIcon.TooltipText = updates.IsChecking ? "C.A.R.S. Control — checking for updates…"
                : n == 0 ? "C.A.R.S. Control — everything is up to date"
                : $"C.A.R.S. Control — {n} update{(n == 1 ? "" : "s")} ready";
        };
    }

    /// <summary>Page to open on launch, from <c>--page Refresh</c> (used by the tray's "Review" action).</summary>
    public static Type? StartPage { get; set; }

    /// <summary>Set before Show() for --background starts: shows minimised off the taskbar, then hides.</summary>
    public bool StartHidden
    {
        get => _startHidden;
        set
        {
            _startHidden = value;
            if (value)
            {
                WindowState = WindowState.Minimized;
                ShowInTaskbar = false;
            }
        }
    }

    public static Type? PageFromName(string? name) => name?.ToLowerInvariant() switch
    {
        "home" => typeof(HomePage),
        "apps" => typeof(AppsPage),
        "refresh" or "updates" => typeof(RefreshPage),
        "storage" => typeof(StoragePage),
        "cleanup" => typeof(CleanupPage),
        "health" => typeof(HealthPage),
        "backup" or "sync" => typeof(BackupPage),
        "privacy" => typeof(PrivacyPage),
        "optimize" or "optimization" => typeof(OptimizePage),
        "history" => typeof(HistoryPage),
        "settings" => typeof(SettingsPage),
        _ => null,
    };

    public void NavigateTo(Type pageType) => RootNavigation.Navigate(pageType);

    private const double WideSidebarWidth = 1180;

    /// <summary>The sidebar's template draws its separators as transparent→grey→transparent gradient
    /// bars (the "weird gradient bars" above Home and Settings). The top one goes; the footer one becomes
    /// a plain inset line in the theme's divider color.</summary>
    private void TidySidebarSeparators()
    {
        if (FindNamed<Border>(RootNavigation, "PART_TopSeparator") is { } top) top.Visibility = Visibility.Collapsed;
        if (FindNamed<Border>(RootNavigation, "PART_FooterSeparator") is { } footer)
        {
            footer.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
            footer.Margin = new Thickness(12, 0, 12, 0);
        }
        // The selected item's marker kept the Windows accent (red here) after the app accent changed.
        foreach (var marker in AllNamed<System.Windows.Shapes.Rectangle>(RootNavigation, "ActiveRectangle"))
            marker.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentFillColorDefaultBrush");
    }

    private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement =>
        AllNamed<T>(root, name).FirstOrDefault();

    private static IEnumerable<T> AllNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) yield return match;
            foreach (var deeper in AllNamed<T>(child, name)) yield return deeper;
        }
    }

    /// <summary>
    /// While the window is open: releases the pages beyond the <see cref="PagesKeptWhileOpen"/> most recently shown, so visiting
    /// every page doesn't keep every page's visual tree. Going back to one rebuilds it (data stays in its view model).
    /// </summary>
    public void TrimPages()
    {
        var cache = App.Services.GetRequiredService<PageCache>();
        var drop = cache.Beyond(PagesKeptWhileOpen, _currentPage);
        if (drop.Count == 0) return;
        ForgetPages(drop.ToHashSet());
        cache.Release(drop, trimWorkingSet: false);
    }

    /// <summary>Releases every page but the current one (see <see cref="PageCache"/>). Runs after three minutes in the tray.</summary>
    public void ReleaseHiddenPages()
    {
        ForgetPages(null);
        App.Services.GetRequiredService<PageCache>().Release(_currentPage);
        // Nothing is drawn while the window is hidden, yet WPF's Direct3D device keeps ~95 MB of graphics memory mapped
        // (write-combined pages, seen with !address -summary). Software rendering while hidden lets it go; ShowFromTray restores it.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }

    /// <summary>Removes the navigation's own references to pages about to be released (all but the current one when null).</summary>
    private void ForgetPages(HashSet<Type>? drop)
    {
        bool Dropped(Type t) => drop is null ? t != _currentPage : drop.Contains(t);
        // WPF-UI shows pages in a Frame whose back-history (journal) keeps every visited page object alive: empty it first.
        if (FindChild<System.Windows.Controls.Frame>(RootNavigation) is { } frame)
            while (frame.CanGoBack) frame.RemoveBackEntry();
        // WPF-UI 4.3's NavigationView also maps every page object it has shown to its sidebar item and never prunes that map
        // (found with dotnet-dump gcroot: it kept all released pages alive). Drop the released pages' entries; WPF-UI adds a page
        // back when it's shown again. Private state of a library: if a WPF-UI update renames it, nothing is freed, nothing breaks.
        try
        {
            if (typeof(Wpf.Ui.Controls.NavigationView).GetProperty("PageToNavigationItemDictionary",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(RootNavigation) is System.Collections.IDictionary map)
                foreach (var page in map.Keys.Cast<object>().Where(p => Dropped(p.GetType())).ToList()) map.Remove(page);
            else PerfLog.Mark("page release: WPF-UI's page map wasn't found (renamed in an update?)");
        }
        catch (Exception ex) when (ex is System.Reflection.TargetException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            PerfLog.Mark("page release: couldn't prune WPF-UI's page map: " + ex.Message);
        }
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    public void ShowFromTray()
    {
        if (App.IsDevInstance)
        {
            DevWindowPlacement.ShowWithoutFocus(this);
            return;
        }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // reliably bring to front from a background thread signal
        Topmost = false;
    }

    /// <summary>Closing the window keeps the app in the tray when "run in background" is on.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        var settings = App.Services.GetRequiredService<AppSettings>();
        if (!_exiting && settings.RunInBackground)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        TrayIcon.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown(0);
    }

    public void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void OnTrayOpen(object sender, RoutedEventArgs e) => ShowFromTray();

    private void OnTrayReview(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        NavigateTo(typeof(RefreshPage));
    }

    private async void OnTrayCheck(object sender, RoutedEventArgs e)
    {
        var updates = App.Services.GetRequiredService<UpdateCenter>();
        await updates.CheckAsync(force: true);
        updates.NotifyIfNew();
    }

    private void OnTrayMenuOpened(object sender, RoutedEventArgs e)
    {
        var maintenance = App.Services.GetRequiredService<MaintenanceService>();
        var waiting = maintenance.Approvals.Count;
        TrayApprove.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrayApprove.Header = waiting == 1 ? "Approve 1 waiting admin item" : $"Approve {waiting} waiting admin items";
        TrayMaintenance.IsEnabled = !maintenance.IsRunning;
        var game = App.Services.GetRequiredService<GameMode>();
        TrayGaming.IsChecked = game.IsOn;
        TrayGaming.Header = game.DetectedGame is { } g ? $"Gaming mode (on: {g})" : "Gaming mode";
        var palette = App.Services.GetRequiredService<AppSettings>().Store.Current.Palette;
        TrayPalette.InputGestureText = App.Services.GetRequiredService<GlobalHotkey>().IsRegistered ? palette.Hotkey : "";
    }

    private void OnTrayApprove(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        NavigateTo(typeof(HomePage));
        _ = App.Services.GetRequiredService<MaintenanceService>().ApproveAsync();
    }

    private void OnTrayMaintenance(object sender, RoutedEventArgs e) =>
        _ = App.Services.GetRequiredService<MaintenanceService>().RunAsync("Maintenance (run now)", unattended: false);

    private void OnTrayGaming(object sender, RoutedEventArgs e) =>
        App.Services.GetRequiredService<GameMode>().SetManual(TrayGaming.IsChecked);

    private void OnTrayPalette(object sender, RoutedEventArgs e) => PaletteWindow.Open(fromHotkey: true);

    private void OnTrayExit(object sender, RoutedEventArgs e) => ExitApp();
}
