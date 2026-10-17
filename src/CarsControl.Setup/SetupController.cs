using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CarsControl.Setup;
using Microsoft.Win32;

namespace CarsControl.SetupApp
{
    internal enum Intent { Install, Update, Repair, Reinstall, Uninstall }

    internal sealed class Options
    {
        public bool StartWithWindows { get; set; }
        public bool LaunchWhenDone { get; set; }
        public bool RemoveUserData { get; set; }
        public bool RemoveMachineData { get; set; }
    }

    internal sealed class InstalledInfo
    {
        public SemVer? Version { get; set; }
        public string? Location { get; set; }
        public bool IsInstalled => Version != null;
    }

    /// <summary>Everything Setup does, with no window: the wizard and --silent both drive this.</summary>
    internal sealed class SetupController
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "CarsControl";

        public InstallLayout Layout { get; }
        public SetupCommandLine Cmd { get; }
        public PayloadManifest? Manifest { get; }
        public string LogFile { get; } = Path.Combine(Path.GetTempPath(), "CarsControl-Setup.log");

        public event Action<int, string>? Progress;
        public event Action<string>? LogLine;

        public SetupController(SetupCommandLine cmd)
        {
            Cmd = cmd;
            Layout = new InstallLayout(cmd.Sandbox);
            Manifest = Embedded.Manifest();
            try { File.WriteAllText(LogFile, "C.A.R.S. Control Setup " + Embedded.SetupVersion + " - " + DateTime.Now + Environment.NewLine); } catch (IOException) { }
        }

        public bool HasPayload => Embedded.HasPayload && Manifest != null;
        public SemVer ThisVersion => SemVer.TryParse(Manifest?.Version ?? Embedded.SetupVersion, out var v) ? v : new SemVer(0, 0, 0);

        public void Log(string text)
        {
            try { File.AppendAllText(LogFile, text + Environment.NewLine); } catch (IOException) { }
            LogLine?.Invoke(text);
        }

        public InstalledInfo Installed()
        {
            var info = new InstalledInfo();
            if (Layout.IsSandbox)
            {
                if (InstallEngine.InstalledVersionFromSandbox(Layout) is { } sv && SemVer.TryParse(sv, out var v)) { info.Version = v; info.Location = Layout.InstallDir; }
                return info;
            }
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(InstallLayout.ArpKey))
            {
                if (key?.GetValue("DisplayVersion") is string text && SemVer.TryParse(text, out var v))
                {
                    info.Version = v;
                    info.Location = key.GetValue("InstallLocation") as string ?? Layout.InstallDir;
                }
            }
            return info;
        }

        public bool StartWithWindowsNow()
        {
            if (Layout.IsSandbox) return File.Exists(Path.Combine(Layout.Sandbox!, "user-run.txt"));
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue(RunValue) is string;
        }

        // ---------- the work ----------

        public Task<InstallResult> RunAsync(Intent intent, Options options, CancellationToken cancel = default) =>
            Task.Run(() => intent == Intent.Uninstall ? Uninstall(options, cancel) : Install(intent, options, cancel), cancel);

        private InstallResult Install(Intent intent, Options options, CancellationToken cancel)
        {
            if (!HasPayload) return Fail("This Setup file has no app inside it (it was built without one). Download the real CarsControl-Setup from the project's Releases page.");
            if (Elevation.RunningAsAnotherAccount())
                return Fail("Setup was started as a different account (an administrator account's password prompt). Close it and start Setup normally, as yourself: it asks for administrator rights by itself, once.");

            Report(1, "Starting");
            Log("Installing C.A.R.S. Control " + Manifest!.Version + (Layout.IsSandbox ? " into the sandbox " + Layout.Sandbox : ""));
            AskAppToQuit();

            InstallResult result;
            if (Layout.IsSandbox)
            {
                var ops = new SandboxOps(Layout, Process.GetCurrentProcess().MainModule!.FileName);
                result = new InstallEngine(Layout, Manifest, Embedded.OpenPayload, ops).Install(Report, Log);
            }
            else if (Elevation.IsElevated())
            {
                Log("Already running with administrator rights as you: no second prompt.");
                result = new InstallEngine(Layout, Manifest, Embedded.OpenPayload, new WindowsOps(Layout)).Install(Report, Log);
            }
            else
            {
                Report(2, "Waiting for administrator approval");
                var o = Elevation.RunAdminHalf("install", false, Report, Log, cancel);
                result = new InstallResult { Ok = o.Ok, Message = o.Message, Upgraded = Installed().IsInstalled };
            }
            if (!result.Ok) return Fail(result.Message);

            // The parts that are yours, done as you.
            SetStartWithWindows(options.StartWithWindows);
            if (options.LaunchWhenDone && !Layout.IsSandbox) Launch();
            Log(result.Message);
            return result;
        }

        private InstallResult Uninstall(Options options, CancellationToken cancel)
        {
            if (Elevation.RunningAsAnotherAccount())
                return Fail("Setup was started as a different account. Close it and start Setup normally, as yourself.");
            Report(1, "Starting");
            Log("Uninstalling C.A.R.S. Control");
            AskAppToQuit();

            InstallResult result;
            if (Layout.IsSandbox)
            {
                var ops = new SandboxOps(Layout, Process.GetCurrentProcess().MainModule!.FileName);
                result = new InstallEngine(Layout, Manifest ?? new PayloadManifest(), Embedded.OpenPayload, ops).Uninstall(options.RemoveMachineData, Report, Log);
            }
            else if (Elevation.IsElevated())
            {
                result = new InstallEngine(Layout, Manifest ?? new PayloadManifest(), Embedded.OpenPayload, new WindowsOps(Layout)).Uninstall(options.RemoveMachineData, Report, Log);
            }
            else
            {
                Report(2, "Waiting for administrator approval");
                var o = Elevation.RunAdminHalf("uninstall", options.RemoveMachineData, Report, Log, cancel);
                result = new InstallResult { Ok = o.Ok, Message = o.Message };
            }
            if (!result.Ok) return Fail(result.Message);

            SetStartWithWindows(false);
            if (options.RemoveUserData && Layout.IsSandbox)
            {
                Log("Sandbox run: your real data folder " + InstallLayout.UserData + " is left alone.");
            }
            else if (options.RemoveUserData)
            {
                Report(97, "Deleting your settings, history and scans");
                try { FolderSwap.DeleteTree(InstallLayout.UserData); Log("Deleted " + InstallLayout.UserData); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log("Couldn't delete " + InstallLayout.UserData + ": " + ex.Message); }
            }
            Report(100, "Done");
            return result;
        }

        private InstallResult Fail(string message)
        {
            Log("FAILED: " + message);
            return new InstallResult { Ok = false, Message = message };
        }

        private void Report(int percent, string text)
        {
            Log("[" + percent + "%] " + text);
            Progress?.Invoke(percent, text);
            // Test runs only: slow enough to photograph the progress screen.
            if (Layout.IsSandbox && Environment.GetEnvironmentVariable("CARS_SETUP_SLOW") == "1") Thread.Sleep(150);
        }

        // ---------- as you ----------

        /// <summary>Asks the running app to exit properly (Gaming mode resumes what it paused, the helper
        /// closes) and waits a few seconds; the admin half closes whatever is left.</summary>
        private void AskAppToQuit()
        {
            if (Layout.IsSandbox) return;
            var location = Installed().Location ?? Layout.InstallDir;
            var exe = Path.Combine(location, "CarsControl.exe");
            var running = RunningFrom(location);
            if (running.Count == 0 || !File.Exists(exe)) return;
            Report(3, "Closing C.A.R.S. Control");
            try
            {
                using (Process.Start(new ProcessStartInfo(exe, "--page quit") { UseShellExecute = false, CreateNoWindow = true })!) { }
            }
            catch (Exception ex) { Log("Couldn't ask the app to close: " + ex.Message); }
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline && RunningFrom(location).Count > 0) Thread.Sleep(250);
        }

        private static System.Collections.Generic.List<int> RunningFrom(string location)
        {
            var prefix = location.TrimEnd('\\') + "\\";
            var ids = new System.Collections.Generic.List<int>();
            foreach (var p in Process.GetProcessesByName("CarsControl"))
            {
                // A process can exit while it's being looked at: .NET Framework then throws anything from Win32Exception to a
                // NullReferenceException inside ProcessModule.FileName (that one left 0.9.0-rc.4's Setup stuck on "Closing").
                try { if (p.MainModule?.FileName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true) ids.Add(p.Id); }
                catch (Exception) { /* gone, or not ours to inspect */ }
                finally { p.Dispose(); }
            }
            return ids;
        }

        private void SetStartWithWindows(bool on)
        {
            if (Layout.IsSandbox)
            {
                var marker = Path.Combine(Layout.Sandbox!, "user-run.txt");
                if (on) File.WriteAllText(marker, Layout.MainExe(Manifest ?? new PayloadManifest()) + " --background"); else if (File.Exists(marker)) File.Delete(marker);
                return;
            }
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey)!)
            {
                if (on) key.SetValue(RunValue, "\"" + Layout.MainExe(Manifest ?? new PayloadManifest()) + "\" --background", RegistryValueKind.String);
                else key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
            Log(on ? "C.A.R.S. Control will start (in the tray) when you sign in." : "C.A.R.S. Control won't start with Windows.");
        }

        /// <summary>Through Explorer, so the app starts as you even if Setup itself is elevated.</summary>
        public void Launch()
        {
            var exe = Layout.MainExe(Manifest ?? new PayloadManifest());
            if (!File.Exists(exe)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = false })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception ex) { Log("Couldn't start the app: " + ex.Message); }
        }
    }
}
