using System.Management;
using CarsControl.Contracts;
using CarsControl.Core.Startup;
using CarsControl.Core.Util;
using Microsoft.Win32;

namespace CarsControl.Core.Elevation;

/// <summary>
/// The Apps › Startup tab's admin parts. Machine-wide Run entries and the common Startup folder are switched the
/// way Task Manager does (a StartupApproved value), services only between automatic, on demand and disabled,
/// and scheduled tasks only on or off. Each op names something that already exists and isn't on a protected list.
/// </summary>
internal static class StartupOps
{
    public static async Task<OpResult> ExecuteAsync(HelperOp op, bool dryRun, Action<string> line, HelperLog log, CancellationToken ct)
    {
        try
        {
            return op switch
            {
                StartupEntryOp e => EntryAsync(e, dryRun, log),
                ServiceStartTypeOp s => await ServiceAsync(s, dryRun, line, log, ct),
                ScheduledTaskToggleOp t => await TaskAsync(t, dryRun, line, log, ct),
                _ => OpResult.Reject("Not a startup operation."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ManagementException or System.Runtime.InteropServices.COMException)
        {
            log.Write($"startup op {op.GetType().Name} failed: {ex.Message}");
            return new OpResult(OpStatus.Failed, -1, ex.Message);
        }
    }

    // ---------- Run entries and the common Startup folder ----------

    private static OpResult EntryAsync(StartupEntryOp op, bool dryRun, HelperLog log)
    {
        if (op.Where is not ("run" or "run32" or "folder")) return OpResult.Reject($"Unknown place '{op.Where}'.");
        if (string.IsNullOrWhiteSpace(op.Name) || op.Name.Length > 260 || op.Name.IndexOfAny(['\\', '/', '\0', ':']) >= 0) return OpResult.Reject("That isn't a valid entry name.");
        if (Inventory.ProtectedApps.IsAlwaysProtectedName(op.Name)) return OpResult.Reject("Salad startup entries are permanently protected.");
        if (op.Where is "run" or "run32")
        {
            using var entry = Registry.LocalMachine.OpenSubKey(op.Where == "run" ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" : @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run");
            if (Inventory.ProtectedApps.IsAlwaysProtectedName(entry?.GetValue(op.Name) as string)) return OpResult.Reject("Salad startup commands are permanently protected.");
        }
        else
        {
            var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), op.Name);
            if (File.Exists(shortcut))
            {
                var isLink = shortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
                var target = isLink ? StartupReader.ShortcutTarget(shortcut) : shortcut;
                if (isLink && (target is null || target.Equals(shortcut, StringComparison.OrdinalIgnoreCase)))
                    return OpResult.Reject("The startup shortcut's target could not be verified; it was left unchanged.");
                if (Inventory.ProtectedApps.IsAlwaysProtectedName(target) || Inventory.ProtectedApps.IsAlwaysProtectedName(StartupReader.PublisherOf(target)))
                    return OpResult.Reject("Salad startup shortcuts are permanently protected.");
            }
        }

        // It has to exist where it claims to.
        var exists = op.Where switch
        {
            "run" => RunHas(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", op.Name),
            "run32" => RunHas(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", op.Name),
            _ => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), op.Name)),
        };
        if (!exists) return new OpResult(OpStatus.NotFound, -1, $"“{op.Name}” isn't there any more.");

        var approvedKey = StartupApproval.MachineExplorerKey + (op.Where switch { "run" => @"\Run", "run32" => @"\Run32", _ => @"\StartupFolder" });
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, $@"Would {(op.Enable ? "enable" : "disable")} “{op.Name}” in HKLM\{approvedKey}");
        using var key = Registry.LocalMachine.CreateSubKey(approvedKey, writable: true);
        key.SetValue(op.Name, op.Enable ? StartupApproval.Enabled() : StartupApproval.Disabled(DateTime.UtcNow), RegistryValueKind.Binary);
        log.Write($"startup {op.Where} {op.Name} {(op.Enable ? "enabled" : "disabled")}");
        return OpResult.Success(op.Enable ? "It will start with Windows again." : "It won't start with Windows any more.");
    }

    private static bool RunHas(string key, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue(name) is not null;
    }

    // ---------- services ----------

    private static async Task<OpResult> ServiceAsync(ServiceStartTypeOp op, bool dryRun, Action<string> line, HelperLog log, CancellationToken ct)
    {
        if (!ServiceRules.IsValidName(op.Service)) return OpResult.Reject("That isn't a valid service name.");
        if (ServiceRules.ScStartType(op.StartType) is not { } sc) return OpResult.Reject($"Unknown start type '{op.StartType}'.");

        // Look the service up ourselves: its program, its maker and its kind decide whether it may be changed.
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", $"SELECT Name, PathName, ServiceType, StartMode FROM Win32_Service WHERE Name='{op.Service.Replace("'", "''")}'");
        var svc = searcher.Get().Cast<ManagementBaseObject>().FirstOrDefault();
        if (svc is null) return new OpResult(OpStatus.NotFound, -1, $"{op.Service} isn't installed.");
        var type = svc["ServiceType"]?.ToString() ?? "";
        if (!type.Contains("Own Process", StringComparison.OrdinalIgnoreCase) && !type.Contains("Share Process", StringComparison.OrdinalIgnoreCase)) return OpResult.Reject("That's a driver, not a service C.A.R.S. changes.");
        var exe = CommandLines.Executable(svc["PathName"]?.ToString());
        var publisher = StartupReader.PublisherOf(exe);
        if (!ServiceRules.IsListable(op.Service, exe, publisher)) return OpResult.Reject("That service belongs to Windows or Microsoft.");
        if (ServiceRules.IsProtected(op.Service, publisher) || Inventory.ProtectedApps.IsAlwaysProtectedName(exe)) return OpResult.Reject("Something on this PC depends on that service, so C.A.R.S. leaves it alone.");

        var args = new[] { "config", op.Service, "start=", sc };
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, HelperOperations.Describe(Path.Combine(Tools.System32, "sc.exe"), args));
        var r = await ProcessRunner.RunAsync(Path.Combine(Tools.System32, "sc.exe"), args, line, TimeSpan.FromSeconds(30), ct);
        log.Write($"service {op.Service} -> {op.StartType}: exit {r.ExitCode}");
        return r.ExitCode == 0
            ? OpResult.Success(op.StartType switch { "auto" => "It will start with Windows.", "manual" => "It starts only when something asks for it.", _ => "It's switched off." })
            : new OpResult(OpStatus.Failed, r.ExitCode, $"Windows refused: {r.Lines.LastOrDefault()}");
    }

    // ---------- scheduled tasks ----------

    private static async Task<OpResult> TaskAsync(ScheduledTaskToggleOp op, bool dryRun, Action<string> line, HelperLog log, CancellationToken ct)
    {
        if (!TaskRules.IsListable(op.TaskPath)) return OpResult.Reject("That isn't a task C.A.R.S. changes (Microsoft's tasks and its own are off limits).");
        if (TaskRules.IsProtected(op.TaskPath)) return OpResult.Reject("Something on this PC depends on that task, so C.A.R.S. leaves it alone.");

        var args = new[] { "/Change", "/TN", op.TaskPath, op.Enable ? "/ENABLE" : "/DISABLE" };
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, HelperOperations.Describe(Path.Combine(Tools.System32, "schtasks.exe"), args));
        var definition = await ProcessRunner.RunAsync(Path.Combine(Tools.System32, "schtasks.exe"), ["/Query", "/TN", op.TaskPath, "/XML"], null, TimeSpan.FromSeconds(15), ct);
        if (definition.ExitCode != 0 || !TaskRules.MayChangeDefinition(definition.Stdout))
            return OpResult.Reject("The task's current commands could not be verified as unprotected; it was left unchanged.");
        var r = await ProcessRunner.RunAsync(Path.Combine(Tools.System32, "schtasks.exe"), args, line, TimeSpan.FromSeconds(30), ct);
        log.Write($"task {op.TaskPath} {(op.Enable ? "enabled" : "disabled")}: exit {r.ExitCode}");
        return r.ExitCode == 0
            ? OpResult.Success(op.Enable ? "The task is on." : "The task is off.")
            : new OpResult(r.Lines.Any(l => l.Contains("does not exist", StringComparison.OrdinalIgnoreCase) || l.Contains("cannot find", StringComparison.OrdinalIgnoreCase)) ? OpStatus.NotFound : OpStatus.Failed, r.ExitCode, $"Windows refused: {r.Lines.LastOrDefault()}");
    }
}
