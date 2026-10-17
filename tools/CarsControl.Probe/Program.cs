using System.Diagnostics;
using CarsControl.Core.Inventory;
using CarsControl.Core.Updates;
using CarsControl.Core.Winget;

// Diagnostic CLI: exercises each inventory source against the real machine without the UI.
//   probe arp | msix | winget | updates
var cmd = args.FirstOrDefault() ?? "winget";
var sw = Stopwatch.StartNew();

switch (cmd)
{
    case "seed-dev":
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "CarsControl.sln"))) repo = repo.Parent;
        if (repo is null || args.Length is not (2 or 3) || (args.Length == 3 && args[2] != "--demo")) throw new ArgumentException("seed-dev needs a destination inside the repository (and optionally --demo).");
        var demo = args.Length == 3;
        var destination = Path.GetFullPath(args[1]);
        if (!destination.StartsWith(repo.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Dev data must stay inside the repository.");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new ArgumentException("Dev data destination must be empty.");
        Directory.CreateDirectory(destination);
        var original = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarsControl");
        foreach (var name in new[] { "settings.json", "inventory.json" })
            if (File.Exists(Path.Combine(original, name))) File.Copy(Path.Combine(original, name), Path.Combine(destination, name));
        if (demo && File.Exists(Path.Combine(destination, "inventory.json")))
        {
            // Screenshots for the public docs: only common, generic apps, and the profile path rewritten.
            string[] generic = ["Steam", "Google Chrome", "Discord", "Microsoft Edge", "Microsoft Visual Studio Code", "Notepad++", "OBS Studio", "Git",
                "PowerShell", "Windows Terminal", "NVIDIA App", "NVIDIA Graphics Driver", "NVIDIA HD Audio", "Microsoft Visual C++", "Microsoft .NET Runtime",
                "Microsoft Windows Desktop Runtime", "Python 3.12", "Node.js", "Epic Games Launcher", "Ubisoft Connect", "PowerToys (Preview) x64", "ShareX",
                "qBittorrent", "Microsoft Store", "Windows Notepad", "Snipping Tool", "Microsoft Photos", "Rufus", "Telegram Desktop", "Everything",
                "Riot Client", "App Installer", "XBOX", "C.A.R.S. Control", "Realtek Audio", "Microsoft Teams", "Windows Camera"];
            var inventoryFile = Path.Combine(destination, "inventory.json");
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var text = File.ReadAllText(inventoryFile).Replace(profile.Replace(@"\", @"\\"), @"C:\\Users\\you", StringComparison.OrdinalIgnoreCase);
            var node = System.Text.Json.Nodes.JsonNode.Parse(text)!;
            if (node["Apps"] is System.Text.Json.Nodes.JsonArray apps)
                foreach (var app in apps.ToList())
                    if (app?["Name"]?.GetValue<string>() is not { } appName || !generic.Any(g => appName.StartsWith(g, StringComparison.OrdinalIgnoreCase))) apps.Remove(app);
            File.WriteAllText(inventoryFile, node.ToJsonString());
        }
        var seed = new CarsControl.Core.Settings.SettingsStore(Path.Combine(destination, "settings.json"));
        seed.Update(s =>
        {
            s.Alerts = new CarsControl.Core.Settings.AlertSettings { Enabled = false, DeviceLabel = "Test PC" };
            s.Sync = new();
            s.Backup = new();
            s.FirstRunUndo = null;
            s.Ui.Clear();
            if (demo) s.Profile = new(); // this PC's holds and choices stay out of the public screenshots
            s.Profile.HoldDriverUpdatesReason = s.Profile.HoldDriverUpdates ? "Temporary test hold" : null;
            s.Profile.KeepCrashEvidenceReason = s.Profile.KeepCrashEvidence ? "Temporary test hold" : null;
        });
        _ = new CarsControl.Core.Data.CarsDb(Path.Combine(destination, "cars.db"));
        Console.WriteLine("Isolated dev preferences and inventory cache prepared; history, alerts, backups and sync start empty.");
        break;
    case "arp":
        var arp = ArpProvider.ReadAll();
        foreach (var g in arp.GroupBy(a => a.Arp!.Hive)) Console.WriteLine($"{g.Key}: {g.Count()}");
        Console.WriteLine($"MSI product codes: {arp.Count(a => a.MsiProductCode is not null)}, quiet: {arp.Count(a => a.QuietUninstallString is not null)}");
        break;

    case "msix":
        var msix = MsixProvider.ReadAll();
        Console.WriteLine($"MSIX packages: {msix.Count}");
        foreach (var m in msix.Take(10)) Console.WriteLine($"  {m.Name} {m.Version}  {m.MsixFamilyName}");
        break;

    case "inventory":
        var snap = await new InventoryService().LoadAsync();
        Console.WriteLine($"apps: {snap.Apps.Count}  winget error: {snap.WingetError ?? "none"}");
        Console.WriteLine($"  winget-linked: {snap.Apps.Count(a => a.WingetId is not null)}, updates: {snap.Apps.Count(a => a.HasUpdate)}, protected: {snap.Apps.Count(a => a.IsProtected)}");
        foreach (var g in snap.Apps.GroupBy(a => a.Channel).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-13} {g.Count(),4}  e.g. {string.Join(" | ", g.Take(4).Select(a => a.Name))}");
        foreach (var u in snap.Apps.Where(a => a.HasUpdate))
            Console.WriteLine($"  UPDATE {u.Name} {u.Version} -> {u.AvailableVersion} [{u.Scope}] {u.WingetId}");
        var measure = Stopwatch.StartNew();
        await InventoryService.MeasureSizesAsync(snap.Apps, _ => { });
        Console.WriteLine($"  measured {snap.Apps.Count(a => a.SizeIsMeasured)} folders in {measure.ElapsedMilliseconds} ms; largest: " +
            string.Join(", ", snap.Apps.OrderByDescending(a => a.SizeBytes ?? 0).Take(5).Select(a => $"{a.Name} {(a.SizeBytes ?? 0) / 1e9:F1} GB")));
        break;

    case "check":
        var inv = await new InventoryService().LoadAsync();
        var checks = await Task.WhenAll(
            UpdateSources.Timed(UpdateSource.Winget, () => Task.FromResult(UpdateSources.Winget(inv))),
            UpdateSources.Timed(UpdateSource.Wsl, () => Task.FromResult(UpdateSources.Wsl(inv))),
            UpdateSources.Timed(UpdateSource.WindowsUpdate, () => UpdateSources.WindowsUpdateAsync(default)),
            UpdateSources.Timed(UpdateSource.Office, () => Task.FromResult(UpdateSources.Office())),
            UpdateSources.Timed(UpdateSource.Npm, () => UpdateSources.NpmAsync(default)),
            UpdateSources.Timed(UpdateSource.Pip, () => UpdateSources.PipAsync(default)),
            UpdateSources.Timed(UpdateSource.PsModule, () => UpdateSources.PsModulesAsync(default)));
        foreach (var c in checks)
        {
            Console.WriteLine($"{c.Source,-14} {c.Items.Count,3} item(s) in {c.Elapsed.TotalSeconds,5:F1}s {(c.Error is null ? "" : "ERROR " + c.Error)}");
            foreach (var i in c.Items) Console.WriteLine($"    {(i.SelectedByDefault ? "[x]" : "[ ]")} {i.Title}  {i.VersionText}  {(i.NeedsAdmin ? "ADMIN" : "user")}  {i.Note}");
        }
        break;

    case "plans":
        var snapP = await new InventoryService().LoadAsync();
        var plans = snapP.Apps.Select(a => (a, plan: CarsControl.Core.Uninstall.UninstallPlanner.Plan(a))).ToList();
        foreach (var g in plans.GroupBy(p => p.plan.Method).OrderByDescending(g => g.Count()))
            Console.WriteLine($"{g.Key,-15} {g.Count(),4}  e.g. {string.Join(" | ", g.Take(3).Select(p => p.a.Name))}");
        foreach (var p in plans.Where(p => p.plan.Method == CarsControl.Core.Uninstall.UninstallMethod.DetectedSilent).Take(6))
            Console.WriteLine($"  detected: {p.a.Name}: {p.plan.Command}");
        Console.WriteLine($"  elevated: {plans.Count(p => p.plan.Elevated)}, silent: {plans.Count(p => p.plan.IsSilent)}");
        break;

    case "leftovers":
        // Simulates "what would be found if this app were already uninstalled" — scans only, changes nothing.
        var snapL = await new InventoryService().LoadAsync();
        foreach (var name in args.Skip(1))
        {
            var target = snapL.Apps.FirstOrDefault(a => a.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (target is null) { Console.WriteLine($"{name}: not found"); continue; }
            var others = snapL.Apps.Where(a => a.Key != target.Key).ToList();
            var scanner = new CarsControl.Core.Uninstall.LeftoverScanner(others);
            Console.WriteLine($"== {target.Name} [{target.Scope}] loc={target.InstallLocation}");
            foreach (var scope in new[] { CarsControl.Core.Uninstall.LeftoverScope.User, CarsControl.Core.Uninstall.LeftoverScope.Machine })
                foreach (var c in scanner.Scan(target, scope))
                    Console.WriteLine($"   {scope,-7} {c.Confidence,-6} {c.Kind,-11} {c.SizeBytes / 1e6,8:F1} MB  {c.Path}  ({c.Reason})");
        }
        break;

    case "leftovers-key":
        // An app that's already gone, as History stores it: leftovers-key "arp:LocalMachine64:Steam App 4001890" "How to Fish"
        var snapK = await new InventoryService().LoadAsync();
        var gone = CarsControl.Core.Uninstall.RemovedApp.FromHistory(args[1], args[2], null);
        Console.WriteLine($"== {gone.Name} [{gone.Scope}] arp={gone.Arp}");
        foreach (var c in new CarsControl.Core.Uninstall.LeftoverScanner(snapK.Apps).Scan(gone, CarsControl.Core.Uninstall.LeftoverScope.User))
            Console.WriteLine($"   {c.Confidence,-6} {c.Kind,-11} {c.SizeBytes / 1e3,8:F1} KB  {c.Path}  ({c.Reason})");
        break;

    case "games":
        var invG = await new InventoryService().LoadAsync();
        await InventoryService.MeasureSizesAsync(invG.Apps, _ => { });
        var (games, libs) = CarsControl.Core.Storage.GameLibraries.Read(invG.Apps);
        foreach (var l in libs) Console.WriteLine($"lib {l.Source,-6} {l.Path,-32} online={l.Online,-5} games={l.Games,3} {l.Bytes / 1e9,7:F1} GB");
        foreach (var g in games.Take(12)) Console.WriteLine($"  {g.Source,-6} {g.Name,-36} {g.SizeBytes / 1e9,6:F1} GB  played={g.LastPlayed:yyyy-MM-dd}  lib={g.Library}");
        Console.WriteLine($"  total {games.Count} games, {games.Sum(g => g.SizeBytes) / 1e9:F0} GB");
        break;

    case "oldwin":
        var invO = await new InventoryService().LoadAsync();
        foreach (var ow in CarsControl.Core.Storage.OldWindowsReport.Find(invO.Apps))
        {
            Console.WriteLine($"old Windows on {ow.Drive}: {ow.TotalBytes / 1e9:F1} GB; other folders kept: {string.Join(", ", ow.KeepFolders)}");
            foreach (var p in ow.Parts) Console.WriteLine($"   {p.Path,-28} {p.Bytes / 1e9,7:F1} GB  used by: {string.Join(", ", p.StillUsedBy)}");
        }
        break;

    case "scan":
        var r = await CarsControl.Core.Storage.FolderScanner.ScanAsync(args.ElementAtOrDefault(1) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null, default);
        Console.WriteLine($"scanned {r.Root.FullPath}: {r.Root.TotalBytes / 1e9:F1} GB in {r.Root.TotalFiles:N0} files, {r.Elapsed.TotalSeconds:F1}s; unreadable {r.UnreadableBytes / 1e9:F1} GB; online-only {r.OnlineOnlyBytes / 1e9:F1} GB");
        void Tree(CarsControl.Core.Storage.DirNode n, int depth)
        {
            foreach (var c in n.Children.Where(c => c.TotalBytes > 2e9).Take(8))
            {
                Console.WriteLine($"   {new string(' ', depth * 2)}{c.Name,-40} {c.TotalBytes / 1e9,7:F1} GB");
                if (depth < 2) Tree(c, depth + 1);
            }
        }
        Tree(r.Root, 0);
        Console.WriteLine($"   largest: {string.Join(" | ", r.Largest.Take(3).Select(f => $"{Path.GetFileName(f.Path)} {f.Bytes / 1e9:F1} GB"))}");
        Console.WriteLine($"   big & stale (>50 MB, >6 mo): {r.OldLarge.Count} files, {r.OldLarge.Sum(f => f.Bytes) / 1e9:F1} GB");
        break;

    case "drivekind":
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            Console.WriteLine($"{d.Name} {d.VolumeLabel,-12} rotational={CarsControl.Core.Storage.DriveKind.IsRotational(d.Name)?.ToString() ?? "unknown"} threads={CarsControl.Core.Storage.DriveKind.ScanParallelism(d.Name)}");
        break;

    case "wiztree":
        // Unelevated WizTree export of a folder (no MFT access needed for a folder), then our parser.
        var wtTarget = args.ElementAtOrDefault(1) ?? Environment.CurrentDirectory;
        var csvOut = Path.Combine(Path.GetTempPath(), $"cars-wiztree-{Guid.NewGuid():N}.csv");
        var wsw = Stopwatch.StartNew();
        using (var wt = Process.Start(new ProcessStartInfo(CarsControl.Core.Storage.WizTreeImport.ExePath, $"\"{wtTarget}\" /export=\"{csvOut}\" /admin=0") { UseShellExecute = false, CreateNoWindow = true })!)
            wt.WaitForExit(120_000);
        var exportMs = wsw.ElapsedMilliseconds;
        var parsed = CarsControl.Core.Storage.WizTreeImport.Parse(csvOut, wtTarget, wsw.Elapsed);
        Console.WriteLine($"export {exportMs} ms, parse total {parsed.Elapsed.TotalMilliseconds:F0} ms: root '{parsed.Root.FullPath}' {parsed.Root.TotalBytes / 1e6:F1} MB, {parsed.Root.TotalFiles} files, {parsed.Root.Children.Count} subfolders");
        foreach (var c in parsed.Root.Children.Take(5)) Console.WriteLine($"   {c.Name,-28} {c.TotalBytes / 1e6,8:F1} MB  own {c.OwnFilesBytes / 1e6,7:F1} MB  ({c.FullPath})");
        Console.WriteLine($"   largest: {string.Join(" | ", parsed.Largest.Take(3).Select(f => $"{Path.GetFileName(f.Path)} {f.Bytes / 1e6:F1} MB"))}");
        var compare = await CarsControl.Core.Storage.FolderScanner.ScanAsync(wtTarget, null, default);
        Console.WriteLine($"   folder-scan cross-check: {compare.Root.TotalBytes / 1e6:F1} MB, {compare.Root.TotalFiles} files");
        File.Delete(csvOut);
        break;

    case "cleanup":
        // Estimates only — nothing is deleted.
        foreach (var rule in CarsControl.Core.Cleanup.CleanupRules.All)
        {
            if (rule.Elevated) { Console.WriteLine($"  [admin] {rule.Id,-22} (estimated by the helper)"); continue; }
            var e = CarsControl.Core.Cleanup.CleanupRules.Estimate(rule);
            Console.WriteLine($"  {(rule.DefaultOn ? "[x]" : "[ ]")} {rule.Id,-22} {e.Bytes / 1e6,9:F1} MB  {e.Items,6} items  {e.Note}");
        }
        break;

    case "specs":
        var sp = CarsControl.Core.Health.SystemSpecsReader.Read();
        Console.WriteLine($"{sp.ComputerName}: {sp.Cpu}, {sp.Cores} cores / {sp.Threads} threads, up to {sp.MaxMHz} MHz");
        foreach (var g in sp.Gpus) Console.WriteLine($"  GPU {g.Name} | driver {g.DriverVersion} | {g.VramBytes / 1024 / 1024} MB | {(g.IsDiscrete ? "discrete" : "integrated")}");
        Console.WriteLine($"  RAM {sp.RamSummary}: {string.Join("; ", sp.Ram.Select(r => $"{r.Maker} {r.Part} in {r.Slot}"))}");
        Console.WriteLine($"  Board {sp.BoardMaker} {sp.Board} | BIOS {sp.BiosVersion} ({sp.BiosDate:d}) | {sp.WindowsEdition} {sp.WindowsVersion} build {sp.WindowsBuild} | up {sp.Uptime.TotalHours:0.#} h");
        break;

    case "gpu":
        foreach (var gr in CarsControl.Core.Health.NvidiaSensors.Read())
            Console.WriteLine($"{gr.Name} ({gr.DriverVersion}): {gr.TempC} C, load {gr.GpuLoadPct}% / mem {gr.MemoryLoadPct}%, core {gr.CoreMHz} MHz, mem {gr.MemoryMHz} MHz, fan {gr.FanPct}%, {gr.PowerWatts:0.#} W of {gr.PowerLimitWatts:0} W, VRAM {gr.VramUsedBytes / 1024 / 1024} / {gr.VramTotalBytes / 1024 / 1024} MB");
        break;

    case "sleep":
        var report = await CarsControl.Core.Health.SleepHealth.ReadAsync(TimeSpan.FromDays(60));
        Console.WriteLine($"{report.Cycles.Count} sleeps ({report.SlowResumes} slow), {report.Stalls.Count} stalls, {report.Blocks.Count} driver blocks. {report.Headline}");
        foreach (var c in report.Cycles.Take(6)) Console.WriteLine($"  {c.SleptAt:MM-dd HH:mm} {c.State,-10} slept {c.Asleep.TotalMinutes:0} min, resume {c.ResumeTime.TotalSeconds:0} s (firmware {c.FirmwareInit.TotalSeconds:0}) because of {c.Why}");
        foreach (var st in report.Stalls.Take(6)) Console.WriteLine($"  STALL {st.At:MM-dd HH:mm:ss} {st.Length.TotalSeconds:0} s");
        foreach (var b in report.Blocks.Take(4)) Console.WriteLine($"  BLOCK {b.At:MM-dd HH:mm} {b.Driver} {b.Device}");
        break;

    case "drives":
        foreach (var dr in CarsControl.Core.Health.DriveHealthReader.Read(includeCounters: args.Contains("--counters")))
        {
            var verdict = CarsControl.Core.Health.DriveHealthRules.Evaluate(dr);
            Console.WriteLine($"{dr.Name,-28} {dr.Kind,-8} {dr.SizeBytes / 1_000_000_000,5} GB  {dr.Health,-8} {dr.LettersText,-14} temp={dr.TempC} wear={dr.WearPercent} hours={dr.PowerOnHours} written={dr.DataWrittenBytes} unsafe={dr.UnsafeShutdowns} from={dr.CountersFrom}  => {verdict.Summary}");
            foreach (var n in verdict.Notes) Console.WriteLine("     " + n);
        }
        break;

    case "network":
        var adapters = CarsControl.Core.Health.NetworkStatus.Adapters();
        foreach (var a in adapters) Console.WriteLine($"  {(a.IsDefaultRoute ? "->" : "  ")} {a.Name,-34} {(a.Up ? "up  " : "down")} {a.SpeedBps / 1_000_000,6} Mbps  {a.Description}");
        Console.WriteLine("VPN: " + CarsControl.Core.Health.NetworkStatus.Vpn(adapters).Summary);
        var ts = await CarsControl.Core.Health.TailscaleStatus.ReadAsync();
        Console.WriteLine($"Tailscale: installed={ts.Installed} state={ts.BackendState} tailnet={ts.Tailnet} self={ts.SelfName} exit-node-offered={ts.OfferingExitNode} routes={string.Join(",", ts.SubnetRoutes)} {ts.Error}");
        foreach (var p in ts.Peers) Console.WriteLine($"  {p.Name,-22} {p.Os,-8} {p.PathText,-18} {p.Address} seen {p.LastSeen:g}");
        if (args.Contains("--netcheck") && await CarsControl.Core.Health.TailscaleStatus.NetCheckAsync() is { } nc)
            Console.WriteLine($"netcheck: udp={nc.Udp} strictNat={nc.StrictNat} nearest={nc.NearestDerp} {nc.NearestDerpMs} ms public={nc.PublicIpv4}");
        break;

    case "activity":
        var act = await CarsControl.Core.Health.ActivityReader.SampleAsync();
        Console.WriteLine($"CPU {act.TotalCpuPercent}%  RAM {act.RamUsedBytes / 1048576} / {act.RamTotalBytes / 1048576} MB  GPU {act.TotalGpuPercent}%  ({act.Processes.Count} processes)");
        foreach (var sort in Enum.GetValues<CarsControl.Core.Health.ActivitySort>())
        {
            Console.WriteLine($"  by {sort}:");
            foreach (var p in act.TopBy(sort, 5)) Console.WriteLine($"    {p.Name,-26} cpu {p.CpuPercent,5}%  gpu {p.GpuPercent,5}%  ram {p.RamBytes / 1048576,6} MB  disk {p.DiskBytesPerSec / 1024,7} KB/s  {p.Role,-8} {(p.CanEnd ? "" : "(protected)")}");
        }
        break;

    case "reboot":
        Console.WriteLine(CarsControl.Core.Health.PendingReboot.Read().Summary);
        var sn = CarsControl.Core.Health.SafetyNet.Read();
        Console.WriteLine($"System Protection on={sn.ProtectionOn} blockedByPolicy={sn.BlockedByPolicy}");
        var wu = CarsControl.Core.Health.WindowsUpdatePolicy.Read();
        Console.WriteLine($"Windows Update: paused until {wu.PausedUntilUtc:u}, exclude drivers={wu.ExcludeDrivers}, defer features={wu.DeferFeatureDays}, active hours {wu.ActiveHoursStart}-{wu.ActiveHoursEnd}, gaming pause={wu.PausedByGamingMode}");
        var hist = await CarsControl.Core.Updates.WindowsUpdateApi.HistoryAsync(8);
        foreach (var h in hist) Console.WriteLine($"  {h.Date:yyyy-MM-dd} {h.ResultText,-10} {h.Title}");
        break;

    case "report":
        var snapshot = new CarsControl.Core.Health.HealthSnapshot(DateTime.Now, CarsControl.Core.Health.SystemSpecsReader.Read(), CarsControl.Core.Health.DriveHealthReader.Read(false),
            CarsControl.Core.Health.NvidiaSensors.Read(), null, null, null, await CarsControl.Core.Health.SleepHealth.ReadAsync(TimeSpan.FromDays(60)), null,
            CarsControl.Core.Health.NetworkStatus.Vpn(CarsControl.Core.Health.NetworkStatus.Adapters()), await CarsControl.Core.Health.TailscaleStatus.ReadAsync(),
            CarsControl.Core.Health.PendingReboot.Read(), CarsControl.Core.Health.WindowsUpdatePolicy.Read(), "probe");
        var outPath = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine(Path.GetTempPath(), "cars-health-report.html");
        File.WriteAllText(outPath, CarsControl.Core.Health.HealthReport.Render(snapshot, redact: !args.Contains("--raw")));
        Console.WriteLine("wrote " + outPath);
        foreach (var f in CarsControl.Core.Health.HealthSummary.Build(snapshot)) Console.WriteLine($"  [{f.Level}] {f.Title} — {f.Detail}");
        break;

    case "crashes":
        var tl = await CarsControl.Core.Health.CrashLog.ReadAsync(TimeSpan.FromDays(180));
        var dumpsOnDisk = CarsControl.Core.Health.DumpFiles.List();
        foreach (var d in dumpsOnDisk) Console.WriteLine($"  dump {d.Name} {d.Written:g} {d.Bytes / 1024} KB");
        foreach (var b in CarsControl.Core.Health.CrashLog.AttachMinidumps(tl.BlueScreens, dumpsOnDisk)) Console.WriteLine($"  BSOD {b.At:g} {b.Name} dump={b.DumpPath}");
        foreach (var x in tl.Shutdowns.Take(5)) Console.WriteLine($"  unexpected shutdown {x.At:g}");
        foreach (var g in await CarsControl.Core.Health.AppCrashes.ReadAsync(TimeSpan.FromDays(90)))
            Console.WriteLine($"  app {g.App}: {g.Crashes} crashes, {g.Hangs} hangs, last {g.Last:g}, module {g.Module}, {g.Reason}");
        Console.WriteLine("cdb: " + (CarsControl.Core.Health.DumpAnalyzer.FindCdb() ?? "not installed"));
        break;

    case "search":
        var q = args.Length > 1 ? args[1] : "firefox";
        foreach (var hit in await CarsControl.Core.Winget.WingetSearch.SearchAsync(q, 12))
            Console.WriteLine($"  {hit.Name,-34} {hit.Id,-32} {hit.Publisher,-24} {hit.Version,-14} [{hit.Source}] {(hit.IsInstalled ? "installed " + hit.InstalledVersion : "")}");
        break;

    case "startup":
        foreach (var si in CarsControl.Core.Startup.StartupReader.ReadRunAndFolders())
            Console.WriteLine($"  {(si.Enabled ? "ON " : "off")} {si.KindText,-14} {si.Name,-34} {si.Publisher,-26} {si.Where}");
        Console.WriteLine("-- services");
        foreach (var si in CarsControl.Core.Startup.StartupReader.ReadServices())
            Console.WriteLine($"  {(si.Enabled ? "ON " : "off")} {si.Name,-40} {si.Publisher,-28} {si.Where} {si.Note}");
        Console.WriteLine("-- tasks");
        foreach (var si in CarsControl.Core.Startup.StartupReader.ReadTasks())
            Console.WriteLine($"  {(si.Enabled ? "ON " : "off")} {si.Id,-50} {si.Publisher,-24} {si.Where}");
        break;

    case "menu":
        foreach (var me in CarsControl.Core.ContextMenu.ContextMenuEntries.Read())
            Console.WriteLine($"  {(me.Hidden ? "hid" : "   ")} {me.Place,-17} {me.Kind,-8} {me.Name,-44} {me.Owner,-26} {me.Warning}");
        Console.WriteLine("C.A.R.S. entries installed: " + CarsControl.Core.ContextMenu.CarsEntries.IsInstalled());
        break;

    case "junk":
        var dl = Environment.GetEnvironmentVariable("USERPROFILE") + "\\Downloads";
        foreach (var (label, opts) in new (string, CarsControl.Core.Storage.JunkOptions)[]
        {
            ("installers", new(dl, [], [], [])),
            ("empty: Documents", new(null, [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)], [], [])),
            ("empty: Pictures", new(null, [Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)], [], [])),
            ("empty: Desktop", new(null, [Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)], [], [])),
            ("logs: LocalAppData", new(null, [], [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)], [])),
            ("logs: AppData", new(null, [], [Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)], [])),
            ("steam redist", new(null, [], [], CarsControl.Core.Storage.GameLibraries.SteamLibraryRoots().Where(Directory.Exists).ToList())),
        })
        {
            var t0 = sw.ElapsedMilliseconds;
            var found = CarsControl.Core.Storage.JunkFinder.Find(opts);
            Console.WriteLine($"  {label,-22} {found.Count,4} items {found.Sum(f => f.Bytes) / 1048576,7} MB  in {sw.ElapsedMilliseconds - t0} ms");
        }
        break;

    case "winget":
    case "updates":
        var w = await WingetProvider.GetInstalledAsync();
        Console.WriteLine($"winget installed catalog: {w.Count} entries, {w.Count(x => x.IsCorrelated)} correlated");
        foreach (var g in w.GroupBy(x => x.Scope)) Console.WriteLine($"  scope {g.Key}: {g.Count()}");
        Console.WriteLine($"  with product codes: {w.Count(x => x.ProductCodes.Count > 0)}, with family names: {w.Count(x => x.FamilyNames.Count > 0)}");
        foreach (var u in w.Where(x => x.AvailableVersion is not null))
            Console.WriteLine($"  UPDATE {u.Id}  {u.InstalledVersion} -> {u.AvailableVersion}  [{u.Scope}]  codes={string.Join(",", u.ProductCodes)}");
        break;
}
Console.WriteLine($"({sw.ElapsedMilliseconds} ms)");
