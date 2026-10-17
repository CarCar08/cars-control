using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CarsControl.Setup
{
    /// <summary>Everything the installer does to Windows itself (registry, shortcuts, permissions, running
    /// processes, the .NET installer). The real version lives in the setup app; the sandbox version below
    /// does the same into a folder, so the whole flow can be tested without admin rights.</summary>
    public interface ISystemOps
    {
        /// <summary>The setup program's own path (copied into the install folder as the uninstaller).</summary>
        string SelfPath { get; }

        /// <summary>Closes C.A.R.S. Control and its admin helper (politely first).</summary>
        void StopApp(Action<string> log);

        /// <summary>Makes sure the .NET Desktop Runtime the app needs is installed.</summary>
        void EnsureRuntime(string required, Action<int, string> progress, Action<string> log);

        /// <summary>Only administrators may write to the folder (it holds the admin helper).</summary>
        void SecureInstallFolder(string dir, Action<string> log);

        /// <summary>ProgramData\CarsControl must be owned by administrators; a standard user's copy is moved aside.</summary>
        void SecureProgramData(Action<string> log);

        void WriteArp(IDictionary<string, object> values);
        void RemoveArp();
        void CreateShortcut(string path, string target, string workingDir, string description);
        void RemoveShortcut(string path);
    }

    public sealed class InstallResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public bool Upgraded { get; set; }
    }

    /// <summary>
    /// The admin half of Setup: unpack the payload next to the install folder, check every file against the
    /// manifest, swap the folders (with rollback), then register the app. Nothing here touches the user's
    /// own profile; the wizard (running as you) does that part.
    /// </summary>
    public sealed class InstallEngine
    {
        public const string SetupFileName = "CarsControl-Setup.exe";

        private readonly InstallLayout _layout;
        private readonly PayloadManifest _manifest;
        private readonly Func<Stream> _openPayload;
        private readonly ISystemOps _ops;

        public InstallEngine(InstallLayout layout, PayloadManifest manifest, Func<Stream> openPayload, ISystemOps ops)
        {
            _layout = layout;
            _manifest = manifest;
            _openPayload = openPayload;
            _ops = ops;
        }

        public InstallResult Install(Action<int, string> progress, Action<string> log)
        {
            var upgraded = false;
            try
            {
                progress(2, "Checking the .NET runtime");
                _ops.EnsureRuntime(_manifest.RequiredRuntime, (p, t) => progress(2 + p * 18 / 100, t), log);

                progress(20, "Preparing");
                FolderSwap.CleanLeftovers(_layout);
                Directory.CreateDirectory(_layout.ProgramFiles);

                progress(22, "Unpacking C.A.R.S. Control " + _manifest.Version);
                using (var zip = _openPayload())
                    SafeZip.Extract(zip, _layout.NewDir, (p, name) => progress(22 + p * 38 / 100, "Unpacking " + name));

                progress(62, "Adding the uninstaller");
                File.Copy(_ops.SelfPath, Path.Combine(_layout.NewDir, SetupFileName), overwrite: true);

                progress(66, "Checking every file");
                var problems = _manifest.Verify(_layout.NewDir).Where(p => p != "Unexpected: " + SetupFileName).ToList();
                if (problems.Count > 0)
                    throw new InvalidDataException("The package is damaged (" + problems.Count + " problem(s)): " + string.Join("; ", problems.Take(3)));

                progress(72, "Installing");
                var swap = FolderSwap.Swap(_layout.NewDir, _layout.InstallDir, _layout.OldDir,
                    beforeSwap: () => _ops.StopApp(log),
                    afterSwap: () =>
                    {
                        progress(80, "Locking the folder to administrators");
                        _ops.SecureInstallFolder(_layout.InstallDir, log);
                        progress(86, "Adding the Start menu shortcut and the Apps entry");
                        _ops.CreateShortcut(_layout.StartMenuShortcut, _layout.MainExe(_manifest), _layout.InstallDir, "Cleanup, Apps, Refresh, Storage");
                        _ops.WriteArp(ArpEntry.Values(_layout, _manifest, _manifest.TotalSize));
                        _ops.SecureProgramData(log);
                    }, log);
                upgraded = swap.PreviousExisted;

                progress(96, "Cleaning up");
                try { FolderSwap.DeleteTree(_layout.OldDir); } catch (IOException ex) { log("Couldn't remove the previous version's folder: " + ex.Message); }
                progress(100, "Done");
                return new InstallResult { Ok = true, Upgraded = upgraded, Message = (upgraded ? "Updated to " : "Installed ") + _manifest.Version };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                log("Install failed: " + ex.Message);
                FolderSwap.CleanLeftovers(_layout);
                var message = ex is IOException && !(ex is FileNotFoundException) && !(ex is DirectoryNotFoundException)
                    ? ex.Message + " A file in " + _layout.InstallDir + " stayed in use for a while (usually antivirus checking the new files, or a window showing that folder). The installed version wasn't changed; run the update again in a minute."
                    : ex.Message;
                return new InstallResult { Ok = false, Upgraded = upgraded, Message = message };
            }
        }

        /// <summary>Removes the app, its shortcut and its Apps entry. <paramref name="removeMachineData"/> also
        /// deletes ProgramData\CarsControl (the helper's logs and quarantine). Your own data (settings, history)
        /// is the wizard's to remove, as you.</summary>
        public InstallResult Uninstall(bool removeMachineData, Action<int, string> progress, Action<string> log)
        {
            try
            {
                progress(5, "Closing C.A.R.S. Control");
                _ops.StopApp(log);
                progress(30, "Removing the Apps entry and shortcut");
                _ops.RemoveArp();
                _ops.RemoveShortcut(_layout.StartMenuShortcut);
                progress(50, "Deleting the program files");
                FolderSwap.DeleteTree(_layout.InstallDir);
                FolderSwap.CleanLeftovers(_layout);
                if (removeMachineData)
                {
                    progress(85, "Deleting the admin helper's logs and quarantine");
                    var data = Path.Combine(_layout.ProgramData, "CarsControl");
                    if (Directory.Exists(data)) FolderSwap.DeleteTree(data);
                }
                progress(100, "Done");
                return new InstallResult { Ok = true, Message = "C.A.R.S. Control was removed." };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log("Uninstall failed: " + ex.Message);
                return new InstallResult { Ok = false, Message = ex.Message };
            }
        }

        /// <summary>The version recorded in the install folder's own manifest-less state: from the main exe is the
        /// app's job; here we read the Add/Remove entry (or the sandbox file) the engine wrote.</summary>
        public static string? InstalledVersionFromSandbox(InstallLayout layout)
        {
            if (!layout.IsSandbox || !File.Exists(layout.SandboxArpFile)) return null;
            using (var doc = JsonDocument.Parse(File.ReadAllText(layout.SandboxArpFile)))
                return doc.RootElement.TryGetProperty("DisplayVersion", out var v) ? v.GetString() : null;
        }
    }

    /// <summary>The same side effects, into a folder (tests, `--sandbox`): arp.json instead of the registry,
    /// a text stand-in instead of a .lnk, nothing killed, no .NET download, no permissions changed.</summary>
    public sealed class SandboxOps : ISystemOps
    {
        private readonly InstallLayout _layout;
        public List<string> Events { get; } = new List<string>();
        public Func<bool>? FailAfterSwap { get; set; }

        public SandboxOps(InstallLayout layout, string selfPath)
        {
            _layout = layout;
            SelfPath = selfPath;
        }

        public string SelfPath { get; }

        public void StopApp(Action<string> log) => Events.Add("stop-app");

        public void EnsureRuntime(string required, Action<int, string> progress, Action<string> log) => Events.Add("runtime:" + required);

        public void SecureInstallFolder(string dir, Action<string> log)
        {
            Events.Add("secure:" + dir);
            if (FailAfterSwap != null && FailAfterSwap()) throw new IOException("simulated failure after the swap");
        }

        public void SecureProgramData(Action<string> log) => Events.Add("secure-programdata");

        public void WriteArp(IDictionary<string, object> values)
        {
            Directory.CreateDirectory(_layout.Sandbox!);
            File.WriteAllText(_layout.SandboxArpFile, JsonSerializer.Serialize(values));
        }

        public void RemoveArp()
        {
            if (File.Exists(_layout.SandboxArpFile)) File.Delete(_layout.SandboxArpFile);
        }

        public void CreateShortcut(string path, string target, string workingDir, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, target + "|" + workingDir + "|" + description);
        }

        public void RemoveShortcut(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
