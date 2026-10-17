using System;
using System.IO;
using System.Linq;

namespace CarsControl.Setup
{
    /// <summary>The folders the installer works with. <paramref name="sandbox"/> redirects everything into a
    /// temp folder for testing (no admin, no Program Files, no registry, no real Start menu).</summary>
    public sealed class InstallLayout
    {
        public string? Sandbox { get; }
        public bool IsSandbox => Sandbox != null;

        public InstallLayout(string? sandbox = null) => Sandbox = sandbox;

        public string ProgramFiles => IsSandbox ? Path.Combine(Sandbox!, "ProgramFiles") : Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        public string InstallDir => Path.Combine(ProgramFiles, "CarsControl");
        public string NewDir => InstallDir + ".new";
        public string OldDir => InstallDir + ".old";
        public string SetupExe => Path.Combine(InstallDir, "CarsControl-Setup.exe");
        public string MainExe(PayloadManifest m) => Path.Combine(InstallDir, m.MainExe);

        public string ProgramData => IsSandbox ? Path.Combine(Sandbox!, "ProgramData") : Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        public string StartMenuDir => IsSandbox
            ? Path.Combine(Sandbox!, "StartMenu")
            : Path.Combine(ProgramData, "Microsoft", "Windows", "Start Menu", "Programs");
        public string StartMenuShortcut => Path.Combine(StartMenuDir, "C.A.R.S. Control.lnk");

        /// <summary>Where Add/Remove Programs reads it (HKLM, 64-bit view). In a sandbox: a JSON file.</summary>
        public const string ArpKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CarsControl";
        public string SandboxArpFile => Path.Combine(Sandbox ?? "", "arp.json");

        /// <summary>The user's data: settings, history, scans, logs.</summary>
        public static string UserData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarsControl");
    }

    /// <summary>
    /// Replaces the install folder in one step: the new version is fully written and verified next to it
    /// (as CarsControl.new), then the old folder is renamed aside and the new one renamed into place. If
    /// anything fails after the old folder moved, it's put back, so there's never a half-installed app.
    /// </summary>
    public static class FolderSwap
    {
        public sealed class Result
        {
            public bool PreviousExisted { get; set; }
        }

        /// <param name="beforeSwap">Runs once the new folder is ready, right before anything is moved (stop
        /// running copies of the app here).</param>
        /// <param name="afterSwap">Runs after the new folder is in place; throw to roll back.</param>
        /// <summary>How long a folder move keeps retrying while something briefly has a file open; tests shorten it.</summary>
        public static int RetryAttempts { get; set; } = 12;
        public static Action<int> Pause { get; set; } = System.Threading.Thread.Sleep;

        /// <summary>
        /// Renames a folder, retrying for about 15 seconds while a file inside is in use. Antivirus and the search indexer open
        /// freshly written files for a moment, and an app that has just exited can take a moment to let go of its files; one such
        /// moment used to fail a whole update (0.9.0-rc.5: "The process cannot access the file…").
        /// </summary>
        public static void MoveFolder(string from, string to, Action<string>? log = null)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Move(from, to);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException)
                                           && !(ex is DirectoryNotFoundException) && attempt < RetryAttempts
                                           && Directory.Exists(from) && !Directory.Exists(to))
                {
                    log?.Invoke("A file in " + from + " is in use (" + ex.Message.TrimEnd('.') + "); trying again (" + attempt + ")…");
                    Pause(Math.Min(250 * attempt, 3000));
                }
            }
        }

        public static Result Swap(string newDir, string targetDir, string oldDir, Action? beforeSwap = null, Action? afterSwap = null, Action<string>? log = null)
        {
            if (!Directory.Exists(newDir)) throw new DirectoryNotFoundException(newDir);
            var result = new Result { PreviousExisted = Directory.Exists(targetDir) };
            beforeSwap?.Invoke();
            if (Directory.Exists(oldDir)) DeleteTree(oldDir);

            var movedOld = false;
            try
            {
                if (result.PreviousExisted)
                {
                    MoveFolder(targetDir, oldDir, log);
                    movedOld = true;
                }
                MoveFolder(newDir, targetDir, log);
                afterSwap?.Invoke();
            }
            catch
            {
                // Put things back the way they were.
                try
                {
                    if (Directory.Exists(targetDir) && (movedOld || !result.PreviousExisted)) MoveAsideAndDelete(targetDir, newDir);
                    if (movedOld && Directory.Exists(oldDir)) MoveFolder(oldDir, targetDir, log);
                }
                catch (IOException)
                {
                    // Leave both folders for a person to sort out rather than make it worse.
                }
                throw;
            }
            return result;
        }

        /// <summary>The new folder is no longer needed after a failure: remove it so the next try starts clean.</summary>
        private static void MoveAsideAndDelete(string folder, string asideName)
        {
            var aside = asideName + ".failed";
            if (Directory.Exists(aside)) DeleteTree(aside);
            Directory.Move(folder, aside);
            DeleteTree(aside);
        }

        public static void DeleteTree(string path)
        {
            if (!Directory.Exists(path)) return;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { }
            }
            Directory.Delete(path, recursive: true);
        }

        public static void CleanLeftovers(InstallLayout layout)
        {
            foreach (var d in new[] { layout.NewDir, layout.OldDir, layout.NewDir + ".failed" })
            {
                try { DeleteTree(d); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
