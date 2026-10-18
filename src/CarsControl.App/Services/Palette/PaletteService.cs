using System.IO;
using CarsControl.App.Converters;
using CarsControl.App.ViewModels;
using CarsControl.App.Views;
using CarsControl.Core.Palette;
using CarsControl.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace CarsControl.App.Services.Palette;

/// <summary>
/// Builds the command palette's results from everything the app already knows (pages, actions, installed
/// apps, games, big files from saved scans, settings) and ranks them with <see cref="FuzzyMatcher"/> plus
/// how often you've picked each one (kept in settings.json).
/// </summary>
public sealed class PaletteService(IServiceProvider services, AppSettings settings)
{
    private static readonly Dictionary<PaletteGroup, int> Limits = new()
    {
        [PaletteGroup.Actions] = 6, [PaletteGroup.Pages] = 4, [PaletteGroup.Apps] = 6,
        [PaletteGroup.Games] = 5, [PaletteGroup.Files] = 5, [PaletteGroup.Settings] = 4,
    };

    /// <summary>A service, or the page on screen: pages come from PageCache (resolving one directly would build a second, hidden copy).</summary>
    private T Get<T>() where T : notnull =>
        typeof(System.Windows.Controls.Page).IsAssignableFrom(typeof(T))
            ? (T)(object)services.GetRequiredService<PageCache>().Get(typeof(T))
            : services.GetRequiredService<T>();

    /// <summary>The saved scans (games, big files) haven't loaded yet.</summary>
    public bool ScansPending => !Get<StorageViewModel>().ReportsLoaded;

    /// <summary>Calls back (on the UI thread) once the saved scans have loaded; gives up after a minute.</summary>
    public void WhenScansLoaded(Action done)
    {
        var storage = Get<StorageViewModel>();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        var ticks = 0;
        timer.Tick += (_, _) =>
        {
            if (storage.ReportsLoaded || ++ticks > 150)
            {
                timer.Stop();
                if (storage.ReportsLoaded) done();
            }
        };
        timer.Start();
    }

    private static void Show(string page) => ((App)System.Windows.Application.Current).ShowMain(page);

    /// <summary>All results, built fresh each time the palette opens (cheap: it reads what's in memory).</summary>
    public List<PaletteItem> Build()
    {
        // Games and big files come from the saved scans, which load when Storage or Home is first shown.
        // Opened straight from the tray, start loading them so they're there the next time it opens.
        var storage = Get<StorageViewModel>();
        if (!storage.ReportsLoaded) _ = storage.LoadReportsAsync();

        var items = new List<PaletteItem>();
        items.AddRange(Actions());
        items.AddRange(Pages());
        items.AddRange(SettingsSections());
        items.AddRange(HealthTabs());
        items.AddRange(Apps());
        items.AddRange(Games());
        items.AddRange(Files());
        items.AddRange(HelpItems());
        return items;
    }

    /// <summary>Ranked rows with group headers. Groups are ordered by their best match, so the first row
    /// (what Enter runs) is always the best result overall.</summary>
    public List<PaletteRow> Search(string query, IReadOnlyList<PaletteItem> items)
    {
        var usage = settings.Store.Current.Palette.Usage;
        var q = query.Trim();
        var scored = new List<PaletteItem>();
        foreach (var item in items)
        {
            if (q.Length == 0)
            {
                if (item.Group is not (PaletteGroup.Actions or PaletteGroup.Pages)) continue;
                item.Score = 0;
            }
            else
            {
                var title = FuzzyMatcher.Score(q, item.Title);
                var keywords = item.Keywords is null ? null : FuzzyMatcher.Score(q, item.Keywords, loose: false) - 150;
                var best = Math.Max(title ?? int.MinValue, keywords ?? int.MinValue);
                if (best == int.MinValue) continue;
                item.Score = best + GroupWeight(item.Group);
            }
            item.Score += FuzzyMatcher.UsageBoost(usage.GetValueOrDefault(item.Id));
            scored.Add(item);
        }

        var rows = new List<PaletteRow>();
        var groups = scored
            .GroupBy(i => i.Group)
            .Select(g => (Group: g.Key, Items: g.OrderByDescending(i => i.Score).Take(Limits[g.Key]).ToList()))
            .OrderByDescending(g => q.Length == 0 ? -(int)g.Group : g.Items[0].Score)
            .ThenBy(g => g.Group);
        foreach (var (group, list) in groups)
        {
            rows.Add(new PaletteRow(null, GroupTitle(group)));
            rows.AddRange(list.Select(i => new PaletteRow(i, null)));
        }
        return rows;
    }

    /// <summary>On equal matches, doing something beats opening a file: "upd" means Update everything,
    /// not a file called update.rpf.</summary>
    private static int GroupWeight(PaletteGroup g) => g switch
    {
        PaletteGroup.Actions => 120,
        PaletteGroup.Pages => 100,
        PaletteGroup.Apps or PaletteGroup.Games => 60,
        PaletteGroup.Settings => 40,
        _ => 0,
    };

    public void Used(PaletteItem item) =>
        settings.Store.Update(s => s.Palette.Usage[item.Id] = s.Palette.Usage.GetValueOrDefault(item.Id) + 1);

    private static string GroupTitle(PaletteGroup g) => g switch
    {
        PaletteGroup.Actions => "Actions",
        PaletteGroup.Pages => "Pages",
        PaletteGroup.Apps => "Apps",
        PaletteGroup.Games => "Games",
        PaletteGroup.Files => "Big files",
        PaletteGroup.Help => "What is…",
        _ => "Settings",
    };

    // ---------- providers ----------

    /// <summary>Every explainer: typing "pawnio" or "what is unsafe shutdowns" opens it.</summary>
    private static IEnumerable<PaletteItem> HelpItems() => CarsControl.Core.Help.HelpTopics.All.Select(t => new PaletteItem
    {
        Id = "help:" + t.Id, Title = t.Title, Subtitle = "What it is and what to do", Keywords = "what is help explain " + t.SearchText,
        Group = PaletteGroup.Help, Icon = SymbolRegular.QuestionCircle24, SearchOnly = true,
        Run = () => Dialogs.HelpAsync(t),
    });

    private IEnumerable<PaletteItem> Actions()
    {
        var updates = Get<UpdateCenter>();
        var maintenance = Get<MaintenanceService>();
        var game = Get<GameMode>();
        var found = updates.Found.Count;

        yield return new PaletteItem
        {
            Id = "action:update-everything", Title = "Update everything", Group = PaletteGroup.Actions, Icon = SymbolRegular.ArrowSync24,
            Subtitle = found == 0 ? "Everything is up to date (checks stale sources first)" : $"{found} update{(found == 1 ? "" : "s")} ready",
            Keywords = "upgrade install all updates",
            Run = () =>
            {
                Show("refresh");
                return Get<RefreshViewModel>().UpdateEverythingAsync();
            },
        };
        yield return new PaletteItem
        {
            Id = "action:check-updates", Title = "Check for updates", Group = PaletteGroup.Actions, Icon = SymbolRegular.ArrowSync24,
            Subtitle = "Every source, now", Keywords = "refresh scan winget windows update",
            Run = () =>
            {
                Show("refresh");
                Get<RefreshViewModel>().CheckCommand.Execute(null);
                return Task.CompletedTask;
            },
        };
        if (maintenance.Approvals.Count is var waiting and > 0)
            yield return new PaletteItem
            {
                Id = "action:approve", Title = $"Approve {waiting} waiting admin item{(waiting == 1 ? "" : "s")}", Group = PaletteGroup.Actions,
                Icon = SymbolRegular.ShieldKeyhole24, Subtitle = maintenance.Approvals.Describe() + " · one admin prompt",
                Keywords = "admin approval uac",
                Run = () =>
                {
                    Show("home");
                    return maintenance.ApproveAsync();
                },
            };
        yield return new PaletteItem
        {
            Id = "action:selfupdate", Title = "Check for a new version of C.A.R.S. Control", Group = PaletteGroup.Actions, Icon = SymbolRegular.ArrowCircleUp24,
            Subtitle = $"You have {SelfUpdateService.Current}", Keywords = "update self upgrade version release github", SearchOnly = true,
            Run = () =>
            {
                Show("settings");
                Get<SettingsPage>().ScrollTo("Updates");
                return Get<SelfUpdateService>().CheckAsync(manual: true);
            },
        };
        yield return new PaletteItem
        {
            Id = "action:maintenance", Title = "Run maintenance now", Group = PaletteGroup.Actions, Icon = SymbolRegular.CalendarSync24,
            Subtitle = maintenance.NextText, Keywords = "scheduled maintenance updates cleanup",
            Run = () =>
            {
                Show("home");
                return maintenance.RunAsync("Maintenance (run now)", unattended: false);
            },
        };
        yield return new PaletteItem
        {
            Id = "action:gaming", Title = game.IsOn ? "Turn Gaming mode off" : "Turn Gaming mode on", Group = PaletteGroup.Actions,
            Icon = SymbolRegular.Games24, Subtitle = game.Describe(), Keywords = "gaming mode game pause",
            Run = () =>
            {
                game.SetManual(!game.IsOn);
                return Task.CompletedTask;
            },
        };
        yield return new PaletteItem
        {
            Id = "action:cleanup", Title = "Clean up", Group = PaletteGroup.Actions, Icon = SymbolRegular.Broom24,
            Subtitle = "The categories ticked on the Cleanup page", Keywords = "clean temp cache junk free space",
            Run = () =>
            {
                Show("cleanup");
                var vm = Get<CleanupViewModel>();
                if (vm.CleanCommand.CanExecute(null)) vm.CleanCommand.Execute(null);
                return Task.CompletedTask;
            },
        };
        yield return new PaletteItem
        {
            Id = "action:free-up", Title = "Free up space: big and old files", Group = PaletteGroup.Actions, Icon = SymbolRegular.Storage24,
            Keywords = "storage disk space large files",
            Run = () =>
            {
                Show("storage");
                Get<StoragePage>().ShowTab(1);
                return Task.CompletedTask;
            },
        };
        // Drives as last read off the UI thread (a spun-down disk would freeze the palette while you type).
        if (DriveSnapshot.Last.Count == 0) BackgroundTasks.Run(DriveSnapshot.ReadAsync(), "palette drives");
        foreach (var d in DriveSnapshot.Last)
        {
            var letter = d.Letter;
            yield return new PaletteItem
            {
                Id = $"action:scan:{letter}", Title = $"Scan drive {letter}", Group = PaletteGroup.Actions, Icon = SymbolRegular.Storage24,
                Subtitle = $"{d.FreeText} · {d.UsedText} used", Keywords = "storage explore treemap size",
                Run = () =>
                {
                    Show("storage");
                    Get<StoragePage>().ShowTab(0);
                    Get<StorageViewModel>().ScanCommand.Execute(letter);
                    return Task.CompletedTask;
                },
            };
        }
        foreach (var (choice, label) in new[] { (ThemeChoice.Dark, "dark"), (ThemeChoice.Light, "light"), (ThemeChoice.System, "the same as Windows") })
            yield return new PaletteItem
            {
                Id = $"action:theme:{choice}", Title = $"Make the theme {label}", Group = PaletteGroup.Actions, Icon = SymbolRegular.DarkTheme24,
                Keywords = "appearance theme dark light mode", SearchOnly = true,
                Run = () =>
                {
                    settings.Store.Update(s => s.Appearance.Theme = choice);
                    AppearanceService.Apply(settings.Appearance);
                    Get<SettingsViewModel>().Reload();
                    return Task.CompletedTask;
                },
            };
        yield return new PaletteItem
        {
            Id = "action:settings-file", Title = "Show settings.json", Group = PaletteGroup.Actions, Icon = SymbolRegular.Document24,
            Keywords = "settings file json backup edit", SearchOnly = true,
            Run = () =>
            {
                Platform.ShowInFileManager(SettingsStore.DefaultPath);
                return Task.CompletedTask;
            },
        };
        yield return new PaletteItem
        {
            Id = "action:logs", Title = "Open the logs folder", Group = PaletteGroup.Actions, Icon = SymbolRegular.Folder24,
            Keywords = "logs errors troubleshooting", SearchOnly = true,
            Run = () =>
            {
                Platform.ShowInFileManager(Path.GetDirectoryName(ErrorReporter.LogPath));
                return Task.CompletedTask;
            },
        };
        yield return new PaletteItem
        {
            Id = "action:exit", Title = "Exit C.A.R.S. Control", Group = PaletteGroup.Actions, Icon = SymbolRegular.Power24,
            Keywords = "quit close", SearchOnly = true,
            Run = () =>
            {
                Get<MainWindow>().ExitApp();
                return Task.CompletedTask;
            },
        };
    }

    private static IEnumerable<PaletteItem> Pages()
    {
        (string Page, string Title, SymbolRegular Icon, string Keywords)[] pages =
        [
            ("home", "Home", SymbolRegular.Home24, "dashboard overview"),
            ("apps", "Apps", SymbolRegular.Apps24, "installed programs uninstall"),
            ("refresh", "Refresh (updates)", SymbolRegular.ArrowSync24, "updates upgrade"),
            ("storage", "Storage", SymbolRegular.Storage24, "disk space drives games files"),
            ("cleanup", "Cleanup", SymbolRegular.Broom24, "clean temp cache"),
            ("health", "Health", SymbolRegular.HeartPulse24, "temperature sensors drives crashes blue screen sleep boot drivers network vpn"),
            ("backup", "Backup & sync", SymbolRegular.CloudArrowUp24, "backup copy usb proton drive setup sync pcs fleet restore"),
            ("privacy", "Privacy", SymbolRegular.ShieldLock24, "telemetry ads copilot recall debloat tracking camera microphone"),
            ("optimize", "Optimization", SymbolRegular.Gauge24, "performance speed power mode game mode gpu scheduling hags hibernation animations transparency mouse acceleration fast startup tweaks"),
            ("history", "History", SymbolRegular.History24, "log activity done"),
            ("settings", "Settings", SymbolRegular.Settings24, "options preferences"),
        ];
        return pages.Select(p => new PaletteItem
        {
            Id = $"page:{p.Page}", Title = p.Title, Group = PaletteGroup.Pages, Icon = p.Icon, Keywords = p.Keywords,
            Run = () =>
            {
                Show(p.Page);
                return Task.CompletedTask;
            },
        });
    }

    private IEnumerable<PaletteItem> HealthTabs()
    {
        (ViewModels.HealthTab Tab, string Title, string Keywords)[] tabs =
        [
            (ViewModels.HealthTab.Sensors, "Temperatures and sensors", "gpu cpu temperature fan voltage hot cooling pawnio"),
            (ViewModels.HealthTab.Drives, "Drive health", "ssd nvme hard disk smart wear errors temperature failing"),
            (ViewModels.HealthTab.Crashes, "Crashes and blue screens", "bsod bugcheck minidump windbg freeze crash"),
            (ViewModels.HealthTab.SleepBoot, "Sleep, wake and start-up times", "hibernate resume slow boot freeze awake power requests"),
            (ViewModels.HealthTab.Drivers, "Drivers", "driver store old packages nvidia version remove cleanup"),
            (ViewModels.HealthTab.Activity, "What's using my PC", "processes cpu gpu memory disk task manager end"),
            (ViewModels.HealthTab.Network, "VPN and network", "proton tailscale exit node nat relay peers"),
        ];
        (string Tab, int Index, string Title, string Keywords)[] appTabs =
        [
            ("get", 1, "Get apps", "install winget store search download new app list bundle"),
            ("startup", 2, "Startup programs", "startup boot login services tasks autorun disable slow"),
            ("menu", 3, "Right-click menu", "context menu explorer clean entries shell extensions"),
        ];
        var appItems = appTabs.Select(t => new PaletteItem
        {
            Id = $"apps:{t.Tab}", Title = t.Title, Subtitle = "Apps", Group = PaletteGroup.Pages, Icon = SymbolRegular.Apps24, Keywords = t.Keywords, SearchOnly = true,
            Run = () =>
            {
                Show("apps");
                Get<AppsPage>().ShowTab(t.Index);
                return Task.CompletedTask;
            },
        });
        (string Tab, int Index, string Title, string Keywords)[] backupTabs =
        [
            ("setup", 1, "Sync my setup", "settings export import proton drive another pc new pc"),
            ("pcs", 2, "Your PCs", "fleet other computers status laptop"),
        ];
        var backupItems = backupTabs.Select(t => new PaletteItem
        {
            Id = $"backup:{t.Tab}", Title = t.Title, Subtitle = "Backup & sync", Group = PaletteGroup.Pages, Icon = SymbolRegular.CloudArrowUp24, Keywords = t.Keywords, SearchOnly = true,
            Run = () =>
            {
                Show("backup");
                Get<BackupPage>().ShowTab(t.Index);
                return Task.CompletedTask;
            },
        });
        var windowsUpdate = new PaletteItem
        {
            Id = "refresh:windowsupdate", Title = "Windows Update settings", Subtitle = "Refresh", Group = PaletteGroup.Pages, Icon = SymbolRegular.ArrowSync24,
            Keywords = "pause updates drivers defer feature active hours hide history", SearchOnly = true,
            Run = () =>
            {
                Show("refresh");
                Get<RefreshPage>().ShowTab(1);
                return Task.CompletedTask;
            },
        };
        return tabs.Select(t => new PaletteItem
        {
            Id = $"health:{t.Tab}", Title = t.Title, Subtitle = "Health", Group = PaletteGroup.Pages, Icon = SymbolRegular.HeartPulse24,
            Keywords = t.Keywords, SearchOnly = true,
            Run = () =>
            {
                Show("health");
                Get<HealthPage>().ShowTab(t.Tab);
                return Task.CompletedTask;
            },
        }).Append(windowsUpdate).Concat(appItems).Concat(backupItems);
    }

    private IEnumerable<PaletteItem> SettingsSections()
    {
        (string Section, string Title, string Keywords)[] sections =
        [
            ("Appearance", "Appearance", "theme dark light accent color background translucent solid"),
            ("Maintenance", "Scheduled maintenance", "schedule days time idle automatic updates cleanup"),
            ("Gaming", "Gaming mode settings", "games game detection pause windows update indexing standby memory"),
            ("Alerts", "Phone alerts", "ntfy notifications phone iphone quiet hours topic"),
            ("Health", "Health settings", "temperature limit hot cpu gpu sensors restore point safety net"),
            ("Palette", "Command palette", "shortcut hotkey keyboard ctrl"),
            ("Background", "Background work", "tray check interval start with windows startup"),
            ("Helper", "Admin helper", "uac admin session idle elevated"),
            ("ThisPc", "This PC", "drivers hold crash dumps evidence"),
            ("Protected", "Protected apps", "protect lock uninstall"),
            ("Ignored", "Ignored updates", "skip ignore versions"),
            ("Updates", "About and updates", "version update release notes github self update repository diagnostics bug report"),
            ("Data", "Data and files", "settings json database quarantine logs"),
        ];
        return sections.Select(s => new PaletteItem
        {
            Id = $"settings:{s.Section}", Title = s.Title, Subtitle = "Settings", Group = PaletteGroup.Settings, Icon = SymbolRegular.Settings24,
            Keywords = s.Keywords, SearchOnly = true,
            Run = () =>
            {
                Show("settings");
                Get<SettingsPage>().ScrollTo(s.Section);
                return Task.CompletedTask;
            },
        });
    }

    private IEnumerable<PaletteItem> Apps()
    {
        var apps = Get<AppsViewModel>();
        foreach (var app in apps.Items)
        {
            var a = app;
            var more = new List<PaletteItem>();
            if (a.HasUpdate)
                more.Add(Sub($"app-update:{a.Entry.Key}", $"Update to {a.AvailableVersion}", SymbolRegular.ArrowSync24, () =>
                {
                    Show("apps");
                    return apps.UpdateNowCommand.ExecuteAsync(a);
                }));
            if (a.CanSelect)
                more.Add(Sub($"app-uninstall:{a.Entry.Key}", "Uninstall…", SymbolRegular.Delete24, () =>
                {
                    Show("apps");
                    return apps.UninstallOneCommand.ExecuteAsync(a);
                }));
            if (a.HasInstallLocation)
                more.Add(Sub($"app-folder:{a.Entry.Key}", "Open install folder", SymbolRegular.FolderOpen24, () =>
                {
                    Platform.ShowInFileManager(a.InstallLocation);
                    return Task.CompletedTask;
                }));
            more.Add(Sub($"app-copy:{a.Entry.Key}", "Copy name", SymbolRegular.Copy24, () =>
            {
                Platform.CopyText(a.Name);
                return Task.CompletedTask;
            }));

            yield return new PaletteItem
            {
                Id = $"app:{a.Entry.Key}", Title = a.Name, Group = PaletteGroup.Apps, Icon = SymbolRegular.Apps24, SearchOnly = true,
                Subtitle = string.Join(" · ", new[] { a.Version, a.Publisher, a.HasUpdate ? $"update to {a.AvailableVersion}" : "" }.Where(x => !string.IsNullOrWhiteSpace(x))),
                Keywords = a.WingetId,
                More = more,
                Run = () =>
                {
                    Show("apps");
                    apps.SearchText = a.Name;
                    apps.ShowDetailsCommand.Execute(a);
                    return Task.CompletedTask;
                },
            };
        }
    }

    private IEnumerable<PaletteItem> Games()
    {
        var storage = Get<StorageViewModel>();
        foreach (var g in storage.AllGames)
        {
            var game = g;
            var more = new List<PaletteItem>();
            if (game.SteamAppId is { } id)
                more.Add(Sub($"game-play:{id}", "Play (Steam)", SymbolRegular.Play24, () =>
                {
                    Platform.OpenUri($"steam://rungameid/{id}");
                    return Task.CompletedTask;
                }));
            if (game.InstallPath is { } path)
                more.Add(Sub($"game-folder:{game.Name}", "Open game folder", SymbolRegular.FolderOpen24, () =>
                {
                    Platform.ShowInFileManager(path);
                    return Task.CompletedTask;
                }));
            yield return new PaletteItem
            {
                Id = $"game:{game.Source}:{game.Name}", Title = game.Name, Group = PaletteGroup.Games, Icon = SymbolRegular.Games24, SearchOnly = true,
                Subtitle = $"{game.Source} · {Format.Bytes(game.SizeBytes)}" + (game.LastPlayed is { } lp ? $" · played {Format.Ago(lp)}" : ""),
                More = more,
                Run = () =>
                {
                    Show("storage");
                    Get<StoragePage>().ShowTab(4);
                    storage.GameSearch = game.Name;
                    return Task.CompletedTask;
                },
            };
        }
    }

    private IEnumerable<PaletteItem> Files()
    {
        var storage = Get<StorageViewModel>();
        foreach (var f in storage.AllFiles.OrderByDescending(f => f.File.Bytes).Take(400))
        {
            var row = f;
            yield return new PaletteItem
            {
                Id = $"file:{row.File.Path}", Title = row.Name, Group = PaletteGroup.Files, Icon = SymbolRegular.Document24, SearchOnly = true,
                Subtitle = $"{Format.Bytes(row.File.Bytes)} · {row.Folder}",
                More =
                [
                    Sub($"file-copy:{row.File.Path}", "Copy path", SymbolRegular.Copy24, () =>
                    {
                        Platform.CopyText(row.File.Path);
                        return Task.CompletedTask;
                    }),
                ],
                Run = () =>
                {
                    Platform.ShowInFileManager(row.File.Path);
                    return Task.CompletedTask;
                },
            };
        }
    }

    private static PaletteItem Sub(string id, string title, SymbolRegular icon, Func<Task> run) =>
        new() { Id = id, Title = title, Icon = icon, Run = run };
}
