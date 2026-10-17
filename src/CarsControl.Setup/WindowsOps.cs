using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using CarsControl.Setup;
using Microsoft.Win32;

namespace CarsControl.SetupApp
{
    /// <summary>The real thing: what the admin half does to Windows. Runs elevated.</summary>
    internal sealed class WindowsOps : ISystemOps
    {
        private readonly InstallLayout _layout;

        public WindowsOps(InstallLayout layout) => _layout = layout;

        public string SelfPath => Process.GetCurrentProcess().MainModule!.FileName;

        // ---------- the running app ----------

        /// <summary>Closes C.A.R.S. Control and its helper, but only copies running from the install folder
        /// (a development build somewhere else is left alone). The wizard already asked the app to exit; this
        /// catches the helper and anything that didn't listen.</summary>
        public void StopApp(Action<string> log)
        {
            var prefix = _layout.InstallDir.TrimEnd('\\') + "\\";
            foreach (var name in new[] { "CarsControl", "CarsControl.Helper" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (path == null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        log("Closing " + name + " (process " + p.Id + ")");
                        try { p.CloseMainWindow(); } catch (InvalidOperationException) { }
                        if (!p.WaitForExit(4000)) p.Kill();
                        p.WaitForExit(8000);
                    }
                    catch (Win32Exception) { /* access denied or already gone */ }
                    catch (InvalidOperationException) { /* exited meanwhile */ }
                    catch (NullReferenceException) { /* exited while its module list was read (.NET Framework) */ }
                    finally { p.Dispose(); }
                }
            }
        }

        // ---------- .NET runtime ----------

        public void EnsureRuntime(string required, Action<int, string> progress, Action<string> log)
        {
            if (DotNetRuntime.IsSatisfied(required))
            {
                log(".NET Desktop Runtime " + required + " is already installed.");
                return;
            }
            log("The .NET Desktop Runtime " + required + " isn't installed: downloading Microsoft's installer.");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // + TLS 1.3 where available
            var temp = Path.Combine(Path.GetTempPath(), "CarsControl-Setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("CarsControl-Setup/" + Embedded.SetupVersion);
                    progress(5, "Looking up the current .NET release");
                    var json = http.GetStringAsync(DotNetRuntime.MetadataUrl(required)).GetAwaiter().GetResult();
                    var download = DotNetRuntime.FindInstaller(json)
                                   ?? throw new InvalidOperationException("Microsoft's release list has no x64 Desktop Runtime installer for .NET " + required + ".");
                    log("Found .NET Desktop Runtime " + download.Version + ".");
                    var file = Path.Combine(temp, Path.GetFileName(download.FileName));

                    using (var response = http.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        var total = response.Content.Headers.ContentLength ?? 0;
                        using (var src = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                        using (var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            var buffer = new byte[81920];
                            long done = 0;
                            int n;
                            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                dst.Write(buffer, 0, n);
                                done += n;
                                if (total > 0) progress((int)(10 + done * 60 / total), "Downloading .NET (" + done / (1024 * 1024) + " of " + total / (1024 * 1024) + " MB)");
                            }
                        }
                    }

                    progress(72, "Checking the download");
                    if (!DotNetRuntime.VerifyDownload(file, download.Sha512))
                        throw new InvalidDataException("The .NET download doesn't match the checksum Microsoft publishes. It was not run.");

                    progress(75, "Installing .NET " + download.Version);
                    using (var installer = Process.Start(new ProcessStartInfo(file, DotNetRuntime.SilentArguments) { UseShellExecute = false, CreateNoWindow = true })!)
                    {
                        installer.WaitForExit();
                        // 0 ok, 3010 ok but a restart is pending, 1638 a newer one is already installed.
                        if (installer.ExitCode != 0 && installer.ExitCode != 3010 && installer.ExitCode != 1638)
                            throw new InvalidOperationException("Microsoft's .NET installer failed (exit code " + installer.ExitCode + ").");
                        log("The .NET installer finished (exit code " + installer.ExitCode + ").");
                    }
                }
                if (!DotNetRuntime.IsSatisfied(required))
                    throw new InvalidOperationException(".NET " + required + " still isn't installed after its installer ran. Restart Windows and run Setup again.");
                progress(100, ".NET is installed");
            }
            finally
            {
                try { FolderSwap.DeleteTree(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // ---------- permissions ----------

        public void SecureInstallFolder(string dir, Action<string> log)
        {
            // Back to the ACL Program Files gives its children: only administrators can write. The admin helper
            // lives here, so a folder a standard user could write to would be a way to run code as admin.
            RunTool("icacls.exe", "\"" + dir + "\" /reset /T /Q");
            var bad = new List<string>();
            var rules = new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            var writeBits = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in rules)
            {
                var sid = (SecurityIdentifier)rule.IdentityReference;
                var everyone = sid.IsWellKnown(WellKnownSidType.WorldSid) || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid) || sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid);
                if (everyone && rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writeBits) != 0) bad.Add(sid.Value);
            }
            if (bad.Count > 0)
                throw new InvalidOperationException("The install folder would be writable by standard users (" + string.Join(", ", bad) + "), so it was not installed. Check the permissions on " + Path.GetDirectoryName(dir) + ".");
            log("The install folder is writable by administrators only.");
        }

        public void SecureProgramData(Action<string> log)
        {
            var pd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CarsControl");
            if (!Directory.Exists(pd)) return;
            string owner;
            try { owner = new DirectoryInfo(pd).GetAccessControl().GetOwner(typeof(NTAccount)).Value; }
            catch (IdentityNotMappedException) { owner = "unknown"; }
            if (owner.IndexOf("Administrators", StringComparison.OrdinalIgnoreCase) >= 0 || owner.IndexOf("SYSTEM", StringComparison.OrdinalIgnoreCase) >= 0
                || owner.IndexOf("TrustedInstaller", StringComparison.OrdinalIgnoreCase) >= 0) return;
            // A standard user created it, so they could steer what the admin helper writes there: move it aside.
            var aside = pd + ".untrusted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Directory.Move(pd, aside);
            log("Moved " + pd + " (owned by " + owner + ") aside to " + aside + ". The helper creates a fresh, locked-down one.");
        }

        // ---------- Apps list and Start menu ----------

        public void WriteArp(IDictionary<string, object> values)
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.CreateSubKey(InstallLayout.ArpKey, true)!)
            {
                foreach (var kv in values)
                {
                    if (kv.Value is int i) key.SetValue(kv.Key, i, RegistryValueKind.DWord);
                    else key.SetValue(kv.Key, Convert.ToString(kv.Value) ?? "", RegistryValueKind.String);
                }
            }
        }

        public void RemoveArp()
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                hklm.DeleteSubKeyTree(InstallLayout.ArpKey, throwOnMissingSubKey: false);
        }

        public void CreateShortcut(string path, string target, string workingDir, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is switched off, so the Start menu shortcut can't be made.");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = workingDir;
            link.IconLocation = target + ",0";
            link.Description = description;
            link.Save();
        }

        public void RemoveShortcut(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private static void RunTool(string exe, string args)
        {
            var path = Path.Combine(Environment.SystemDirectory, exe);
            using (var p = Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new InvalidOperationException(exe + " failed (exit code " + p.ExitCode + ").");
            }
        }
    }
}
