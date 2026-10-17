using System.IO;
using System.Security.Principal;
using System.Windows;
using CarsControl.App.Services;
using CarsControl.App.ViewModels;
using CarsControl.App.Views;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Inventory;
using CarsControl.Core.Uninstall;
using Microsoft.Extensions.DependencyInjection;

namespace CarsControl.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>CARS_DEV_INSTANCE=1: a test build that runs alongside the installed copy (separate single-instance names).</summary>
    public static bool IsDevInstance => Environment.GetEnvironmentVariable("CARS_DEV_INSTANCE") == "1";

    /// <summary>Test copy for the public screenshots (CARS_DEMO=1): keeps the seeded demo app list instead of scanning this PC, and
    /// shows a neutral PC name. Only honoured in a test copy.</summary>
    public static bool IsDemo => IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEMO") == "1";

    public static string PcName => IsDemo ? "MY-PC" : Environment.MachineName;
    public static bool IsShuttingDown { get; private set; }

    private Mutex? _singleInstance;
    private EventWaitHandle? _activate;
    private static string InstanceSuffix => IsDevInstance ? ".dev" : "";

    private static string ActivationFile
    {
        get
        {
            var dir = Core.Util.UserData.Root;
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"activate{InstanceSuffix}.txt");
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ErrorReporter.Install(this);
        ColumnHelp.Install();

        // Elevated = running as the admin account here, which would show that account's per-user apps and put
        // per-user operations in the wrong profile. Admin work belongs in the helper instead.
        if (IsElevated())
        {
            MessageBox.Show(
                "C.A.R.S. Control must run as your normal account, not as administrator.\n\n" +
                "It asks for admin rights by itself, once per session, only for the tasks that need them.",
                "C.A.R.S. Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        if (e.Args.Contains("--selftest-helper"))
        {
            _ = SelfTest.RunAsync().ContinueWith(_ => Dispatcher.Invoke(() => Shutdown(0)));
            return;
        }

        // Single instance: a second launch (Start menu, toast click, `--page refresh`) brings the first one
        // forward and passes along which page to open.
        _singleInstance = new Mutex(true, $@"Local\CarsControl.SingleInstance{InstanceSuffix}", out var isFirst);
        var activateName = $@"Local\CarsControl.Activate{InstanceSuffix}";
        var requestedPage = Array.IndexOf(e.Args, "--page") is var i and >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null;
        // The right-click "Folder size in C.A.R.S." entry: `--explore "D:\Some Folder"`, checked before it goes anywhere.
        if (Array.IndexOf(e.Args, "--explore") is var ex and >= 0 && ex + 1 < e.Args.Length && CarsControl.Core.ContextMenu.ExploreArgument.Validate(e.Args[ex + 1]) is { } explored)
            requestedPage = CarsControl.Core.ContextMenu.ExploreArgument.ToActivation(explored);
        if (!isFirst)
        {
            try
            {
                File.WriteAllText(ActivationFile, requestedPage ?? "");
                EventWaitHandle.OpenExisting(activateName).Set();
            }
            catch
            {
                // First instance still starting.
            }
            Shutdown(0);
            return;
        }
        PerfLog.Mark("startup (single-instance check passed)");
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, activateName);
        new Thread(() =>
        {
            while (_activate.WaitOne())
            {
                string? page = null;
                try { page = File.ReadAllText(ActivationFile).Trim(); } catch { /* none */ }
                Dispatcher.BeginInvoke(() => ShowMain(string.IsNullOrEmpty(page) ? null : page));
            }
        }) { IsBackground = true, Name = "Activate" }.Start();

        Services = ConfigureServices();
        Notifier.Initialize();
        Notifier.Activated += page => ShowMain(page);
        Notifier.ActionInvoked += action =>
        {
            if (action == "selfupdate") _ = Services.GetRequiredService<SelfUpdateService>().ApplyAsync();
            if (action == "restart-cancel") Services.GetRequiredService<HealthWatcher>().CancelRestart();
            if (action != "approve") return;
            ShowMain("home");
            _ = Services.GetRequiredService<MaintenanceService>().ApproveAsync();
        };

        var pageArg = Array.IndexOf(e.Args, "--page");
        if (pageArg >= 0 && pageArg + 1 < e.Args.Length)
            global::CarsControl.App.MainWindow.StartPage = global::CarsControl.App.MainWindow.PageFromName(e.Args[pageArg + 1]);

        var settings = Services.GetRequiredService<AppSettings>();
        AppearanceService.Apply(settings.Appearance); // before the first window exists (see AppearanceService)
        RecommendationContext.Set(settings.Profile.PcType);
        BackgroundTasks.Run(Task.Run(() => RecommendationContext.HasBattery), "battery detection");
        var store = Services.GetRequiredService<InventoryStore>();
        store.Protected = new ProtectedApps(settings.ProtectedApps);
        store.LoadCache(); // instant first paint from the last scan
        Core.Storage.CloudSync.HiddenForScreenshots = IsDemo;
        Extras.Init(store.Current?.Apps.Select(a => a.Name) ?? []);
        PerfLog.Mark("inventory cache loaded");

        var background = e.Args.Contains("--background");
        var window = Services.GetRequiredService<MainWindow>();
        window.StartHidden = background;
        window.Show();
        PerfLog.Mark("window shown");
        if (requestedPage is not null && CarsControl.Core.ContextMenu.ExploreArgument.FromActivation(requestedPage) is not null)
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => ShowMain(requestedPage));

        // A brand-new install gets the first-time setup (someone upgrading from an earlier version doesn't;
        // Settings › About has "Run the first-time setup again").
        if (settings.Store.IsFreshInstall && settings.Store.Current.FirstRunCompleted is null && !IsDevInstance && !background)
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => FirstRunWindow.Open());

        BackgroundTasks.Run(Task.Run(() => Quarantine.Purge(Quarantine.UserRoot, TimeSpan.FromDays(30))), "quarantine expiry");
        BackgroundTasks.Run(store.RefreshAsync(), "initial inventory");

        var updates = Services.GetRequiredService<UpdateCenter>();
        if (settings.RunInBackground) updates.StartBackground();
        Services.GetRequiredService<MaintenanceService>().Start();
        Services.GetRequiredService<GamingEffects>().Start(atSignIn: background);
        Services.GetRequiredService<GameWatcher>().Start();
        Services.GetRequiredService<SelfUpdateService>().Start();
        Services.GetRequiredService<AlertService>().Start();
        Services.GetRequiredService<HealthWatcher>().Start();
        Services.GetRequiredService<SyncService>().Start();
        Services.GetRequiredService<BackupService>().Start();
    }

    public void ShowMain(string? page)
    {
        var w = Services.GetRequiredService<MainWindow>();
        // The right-click entries: show that folder (or drive) in Storage › Explore.
        if (CarsControl.Core.ContextMenu.ExploreArgument.FromActivation(page) is { } folder)
        {
            w.ShowFromTray();
            Navigator.Go("storage", "explore");
            _ = Services.GetRequiredService<StorageViewModel>().ExplorePathAsync(folder);
            return;
        }
        // Test copies: `CarsControl.exe --page dump` writes the visual tree and theme resources right now.
        if (page == "dump" && DevVisualDump.Enabled)
        {
            DevVisualDump.Write(w);
            return;
        }
        // `--page firstrun` opens the first-time setup (also used by the screen-capture tour).
        if (page == "firstrun")
        {
            w.ShowFromTray();
            FirstRunWindow.Open();
            return;
        }
        // `--page quit` (Setup, before it replaces the files): exit the app properly, so Gaming mode resumes
        // what it paused and the admin helper closes.
        if (page == "quit")
        {
            w.ExitApp();
            return;
        }
        if (page == "tray" && IsDevInstance) { w.Hide(); PerfLog.Memory("tray"); PerfLog.Mark("activation handled: tray"); return; }
        if (page == "perf" && IsDevInstance) { PerfLog.Memory("snapshot"); PerfLog.Mark("activation handled: perf"); return; }
        if (page == "release" && IsDevInstance) { w.ReleaseHiddenPages(); PerfLog.Memory("after release"); PerfLog.Mark("activation handled: release"); return; }
        // Test copies: `--page tabaudit` walks the keyboard focus order of every page (see DevTabAudit).
        if (page == "tabaudit" && DevTabAudit.Enabled)
        {
            _ = DevTabAudit.RunAsync(w);
            return;
        }
        // `CarsControl.exe --page palette` (a Start menu or Stream Deck shortcut) opens the command palette.
        if (page == "palette")
        {
            PaletteWindow.Open(fromHotkey: true);
            return;
        }
        w.ShowFromTray();
        if (global::CarsControl.App.MainWindow.PageFromName(page) is { } p) w.NavigateTo(p);
        if (IsDevInstance) w.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => PerfLog.Mark($"activation handled: {page}"));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsShuttingDown = true;
        CarsControl.Core.Updates.WindowsUpdateApi.Stop();
        if (Services is not null)
        {
            Services.GetService<UpdateCenter>()?.StopBackground();
            Services.GetService<InventoryStore>()?.Stop();
            Services.GetService<GameWatcher>()?.Stop(resetMode: false);
            Services.GetService<HealthWatcher>()?.Stop();
            Services.GetService<MaintenanceService>()?.Stop();
            Services.GetService<BackupService>()?.Stop();
            Services.GetService<SyncService>()?.Stop();
            Services.GetService<AlertService>()?.Stop();
            Services.GetService<SelfUpdateService>()?.Stop();
            Services.GetService<GamingEffects>()?.Stop();
        }
        // Anything Gaming mode paused is resumed first, while the admin session still exists.
        // (On the thread pool: waiting here for UI-thread continuations would deadlock.)
        if (Services?.GetService<GamingEffects>() is { } gaming)
            try { Task.Run(gaming.ResumeAtExitAsync).Wait(8000); } catch (AggregateException) { /* best effort */ }
        // Closing the pipe ends the admin helper immediately; it never outlives the app.
        if (Services?.GetService<HelperClient>() is { } helper)
            try { helper.DisposeAsync().AsTask().Wait(2000); } catch (AggregateException ex) { ErrorReporter.Log("helper shutdown", ex); }
        _activate?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static IServiceProvider ConfigureServices()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<CarsDb>();
        sc.AddSingleton<AppSettings>();
        sc.AddSingleton(sp => new HelperClient { IdleMinutes = sp.GetRequiredService<AppSettings>().HelperIdleMinutes });
        sc.AddSingleton<InventoryStore>();
        sc.AddSingleton<UpdateCenter>();
        sc.AddSingleton<UpdateRunner>();
        sc.AddSingleton<CleanupRunner>();
        sc.AddSingleton<AttentionService>();
        sc.AddSingleton<ApprovalQueue>();
        sc.AddSingleton<GameMode>();
        sc.AddSingleton<MaintenanceService>();
        sc.AddSingleton<GameWatcher>();
        sc.AddSingleton<GamingEffects>();
        sc.AddSingleton<SelfUpdateService>();
        sc.AddSingleton<AlertService>();
        sc.AddSingleton<SyncService>();
        sc.AddSingleton<BackupService>();
        sc.AddSingleton<HealthWatcher>();
        sc.AddSingleton<Services.Palette.PaletteService>();
        sc.AddSingleton<GlobalHotkey>();
        sc.AddSingleton<IconLoader>();

        sc.AddSingleton<HomeViewModel>();
        sc.AddSingleton<AppsViewModel>();
        sc.AddSingleton<RefreshViewModel>();
        sc.AddSingleton<HistoryViewModel>();
        sc.AddSingleton<StorageViewModel>();
        sc.AddSingleton<CleanupViewModel>();
        sc.AddSingleton<PrivacyViewModel>();
        sc.AddSingleton<HealthViewModel>();
        sc.AddSingleton<WindowsUpdateViewModel>();
        sc.AddSingleton<StartupViewModel>();
        sc.AddSingleton<GetAppsViewModel>();
        sc.AddSingleton<ContextMenuViewModel>();
        sc.AddSingleton<BackupsViewModel>();
        sc.AddSingleton<DuplicatesViewModel>();
        sc.AddSingleton<JunkViewModel>();
        sc.AddSingleton<MoveFoldersViewModel>();
        sc.AddSingleton<SetupSyncViewModel>();
        sc.AddSingleton<SettingsViewModel>();

        // Pages are built through PageCache (kept while the window is used, released after a while in the tray).
        sc.AddSingleton(sp => new PageCache(sp));
        sc.AddTransient<HomePage>();
        sc.AddTransient<AppsPage>();
        sc.AddTransient<RefreshPage>();
        sc.AddTransient<StoragePage>();
        sc.AddTransient<CleanupPage>();
        sc.AddTransient<PrivacyPage>();
        sc.AddTransient<HealthPage>();
        sc.AddTransient<BackupPage>();
        sc.AddTransient<HistoryPage>();
        sc.AddTransient<SettingsPage>();

        sc.AddSingleton<MainWindow>();
        return sc.BuildServiceProvider();
    }

    internal static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
