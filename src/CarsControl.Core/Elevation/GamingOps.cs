using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using CarsControl.Contracts;
using Microsoft.Win32;

namespace CarsControl.Core.Elevation;

/// <summary>
/// Gaming mode's admin parts, inside the helper. Each takes no paths or command lines: Windows Update is
/// paused through its own pause values (the ones Settings › Windows Update › Pause writes), the standby
/// list is purged with the documented system call ISLC uses, and only Windows Search can be stopped/started.
/// </summary>
internal static class GamingOps
{
    /// <summary>Services Gaming mode may stop and start. Nothing else is accepted.</summary>
    public static readonly IReadOnlySet<string> AllowedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WSearch" };

    public static OpResult Execute(HelperOp op, bool dryRun, Action<string> line, HelperLog log)
    {
        try
        {
            return op switch
            {
                WindowsUpdatePauseOp p => PauseWindowsUpdate(p.Pause, dryRun, log),
                PurgeStandbyListOp => PurgeStandby(dryRun, log),
                ServiceControlOp s => ControlService(s, dryRun, log),
                _ => OpResult.Reject("Not a gaming operation."),
            };
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            log.Write($"gaming op {op.GetType().Name} failed: {ex.Message}");
            return new OpResult(OpStatus.Failed, -1, ex.Message);
        }
    }

    // ---------- Windows Update pause ----------

    private const string UxSettings = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const string Marker = CarsControl.Core.Health.WindowsUpdatePolicy.GamingMarker;

    internal static readonly string[] PauseStartValues = ["PauseUpdatesStartTime", "PauseFeatureUpdatesStartTime", "PauseQualityUpdatesStartTime"];
    internal static readonly string[] PauseEndValues = ["PauseUpdatesExpiryTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesEndTime"];

    /// <summary>Windows stores pause times as UTC "2026-10-13T03:00:00Z".</summary>
    internal static string PauseTime(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>True when a pause that's still running is recorded (yours or ours).</summary>
    internal static bool IsPaused(string? expiry, DateTime utcNow) =>
        DateTime.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until) && until > utcNow;

    private static OpResult PauseWindowsUpdate(bool pause, bool dryRun, HelperLog log)
    {
        using var key = Registry.LocalMachine.CreateSubKey(UxSettings, writable: true);
        var ours = key.GetValue(Marker) is not null;
        if (pause)
        {
            if (!ours && IsPaused(key.GetValue("PauseUpdatesExpiryTime") as string, DateTime.UtcNow))
                return OpResult.Success("Windows Update was already paused (by you) — left as it is.", "already-paused");
            if (dryRun) return new OpResult(OpStatus.DryRun, 0, $@"Would set HKLM\{UxSettings} pause values until 12 h from now");
            var now = DateTime.UtcNow;
            foreach (var v in PauseStartValues) key.SetValue(v, PauseTime(now), RegistryValueKind.String);
            // A safety net: if C.A.R.S. never gets to resume (crash, power cut), Windows resumes by itself.
            foreach (var v in PauseEndValues) key.SetValue(v, PauseTime(now.AddHours(12)), RegistryValueKind.String);
            key.SetValue(Marker, 1, RegistryValueKind.DWord);
            log.Write("gaming: Windows Update paused (12 h expiry)");
            return OpResult.Success("Windows Update paused until you stop playing.", "paused");
        }

        if (!ours) return OpResult.Success("Windows Update wasn't paused by C.A.R.S. — left as it is.", "not-ours");
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, $@"Would remove the pause values under HKLM\{UxSettings}");
        foreach (var v in PauseStartValues.Concat(PauseEndValues).Append(Marker)) key.DeleteValue(v, throwOnMissingValue: false);
        log.Write("gaming: Windows Update resumed");
        return OpResult.Success("Windows Update resumed.", "resumed");
    }

    // ---------- standby list ----------

    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    private static OpResult PurgeStandby(bool dryRun, HelperLog log)
    {
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, "Would empty the standby list");
        Privileges.Enable("SeProfileSingleProcessPrivilege");
        var before = StandbyBytes();
        var command = MemoryPurgeStandbyList;
        var status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        if (status != 0) return new OpResult(OpStatus.Failed, status, $"Couldn't empty the standby list (NTSTATUS 0x{status:X8}).");
        var freed = Math.Max(0, before - StandbyBytes());
        log.Write($"gaming: standby list purged, {freed / (1024 * 1024)} MB");
        return OpResult.Success($"Freed {freed / (1024 * 1024)} MB of standby memory.", freed.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Standby pages (all eight priorities) from SYSTEM_MEMORY_LIST_INFORMATION.</summary>
    private static long StandbyBytes()
    {
        const int fields = 5 + 8 + 8 + 1; // zero, free, modified, modified-no-write, bad, standby[8], repurposed[8], modified-pagefile
        var size = fields * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQuerySystemInformation(SystemMemoryListInformation, buffer, size, out _) != 0) return 0;
            long pages = 0;
            for (var i = 5; i < 13; i++) pages += Marshal.ReadIntPtr(buffer, i * IntPtr.Size).ToInt64();
            return pages * Environment.SystemPageSize;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------- services ----------

    private static OpResult ControlService(ServiceControlOp op, bool dryRun, HelperLog log)
    {
        if (!AllowedServices.Contains(op.Service)) return OpResult.Reject($"Service '{op.Service}' is not on Gaming mode's list.");
        var running = Services.IsRunning(op.Service);
        if (running is null) return new OpResult(OpStatus.NotFound, -1, $"{op.Service} isn't installed.");
        var data = running.Value ? "was-running" : "was-stopped";
        if (op.Start == running.Value) return OpResult.Success(op.Start ? "Already running." : "Already stopped.", data);
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, $"Would {(op.Start ? "start" : "stop")} {op.Service}");
        if (op.Start) Services.Start(op.Service);
        else Services.Stop(op.Service);
        log.Write($"gaming: {(op.Start ? "started" : "stopped")} {op.Service}");
        return OpResult.Success(op.Start ? "Search indexing resumed." : "Search indexing paused.", data);
    }

    private static class Services
    {
        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryStatus = 0x0004, ServiceStart = 0x0010, ServiceStop = 0x0020;
        private const uint ControlStop = 0x00000001;
        private const int StateRunning = 4, StateStopped = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public int Type, CurrentState, ControlsAccepted, Win32ExitCode, SpecificExitCode, CheckPoint, WaitHint;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool StartService(IntPtr service, int argc, IntPtr argv);

        [DllImport("advapi32.dll")]
        private static extern bool CloseServiceHandle(IntPtr handle);

        private static T With<T>(string name, uint access, Func<IntPtr, T> use)
        {
            var scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero) throw new Win32Exception();
            try
            {
                var svc = OpenService(scm, name, access);
                if (svc == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == 1060) return default!; // ERROR_SERVICE_DOES_NOT_EXIST
                    throw new Win32Exception(err);
                }
                try { return use(svc); }
                finally { CloseServiceHandle(svc); }
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }

        public static bool? IsRunning(string name) =>
            With<bool?>(name, ServiceQueryStatus, s => QueryServiceStatus(s, out var st) ? st.CurrentState == StateRunning : throw new Win32Exception());

        public static void Stop(string name) => With(name, ServiceStop | ServiceQueryStatus, s =>
        {
            if (!ControlService(s, ControlStop, out _)) throw new Win32Exception();
            return Wait(s, StateStopped);
        });

        public static void Start(string name) => With(name, ServiceStart | ServiceQueryStatus, s =>
        {
            if (!StartService(s, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056) throw new Win32Exception(); // 1056: already running
            return Wait(s, StateRunning);
        });

        private static bool Wait(IntPtr s, int state)
        {
            for (var i = 0; i < 60; i++)
            {
                if (QueryServiceStatus(s, out var st) && st.CurrentState == state) return true;
                Thread.Sleep(250);
            }
            return false;
        }
    }
}

/// <summary>Enables a privilege the admin token holds but has switched off by default.</summary>
internal static class Privileges
{
    private const uint TokenAdjustPrivileges = 0x0020, TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static void Enable(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception();
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            if (Marshal.GetLastWin32Error() == 1300) throw new UnauthorizedAccessException($"This account doesn't hold {name}."); // ERROR_NOT_ALL_ASSIGNED
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
