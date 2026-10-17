using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using CarsControl.Setup;

namespace CarsControl.SetupApp
{
    /// <summary>
    /// Setup runs as you. The part that needs administrator rights is this same exe started with "runas" (one
    /// prompt), which reads the app out of its own image and reports progress over a pipe. It takes no commands
    /// from the pipe: the only things it's told are the two arguments it was started with.
    /// </summary>
    internal static class Elevation
    {
        public static bool IsElevated()
        {
            using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>Whose desktop this is: the owner of explorer.exe in our session. If Setup was started
        /// elevated as a different account (the admin account's password prompt), "start with Windows" and the
        /// data folder would land in the wrong profile, so that's refused.</summary>
        public static SecurityIdentifier? InteractiveUser()
        {
            var session = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    if (p.SessionId != session) continue;
                    var handle = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, p.Id);
                    if (handle == IntPtr.Zero) continue;
                    try
                    {
                        if (!OpenProcessToken(handle, 0x0008 /* TOKEN_QUERY */, out var token)) continue;
                        try { using (var id = new WindowsIdentity(token)) return id.User; }
                        finally { CloseHandle(token); }
                    }
                    finally { CloseHandle(handle); }
                }
                finally { p.Dispose(); }
            }
            return null;
        }

        public static bool RunningAsAnotherAccount()
        {
            if (!IsElevated()) return false;
            var shellUser = InteractiveUser();
            using (var id = WindowsIdentity.GetCurrent()) return shellUser != null && id.User != shellUser;
        }

        public sealed class Outcome
        {
            public bool Ok { get; set; }
            public bool Cancelled { get; set; }
            public string Message { get; set; } = "";
        }

        /// <summary>Starts the admin half and relays its progress. One UAC prompt (none if already elevated as you).</summary>
        public static Outcome RunAdminHalf(string action, bool removeMachineData, Action<int, string> progress, Action<string> log, CancellationToken cancel)
        {
            var pipeName = SetupCommandLine.NewPipeName();
            var security = new PipeSecurity();
            using (var me = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

            using (var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security))
            {
                var self = Process.GetCurrentProcess().MainModule!.FileName;
                var args = "--elevated --pipe " + pipeName + " --action " + action + (removeMachineData ? " --remove-machine-data" : "");
                Process child;
                try
                {
                    child = Process.Start(new ProcessStartInfo(self, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    return new Outcome { Cancelled = true, Message = "The administrator prompt was cancelled, so nothing was changed." };
                }

                using (child)
                {
                    var connect = server.WaitForConnectionAsync(cancel);
                    var waited = 0;
                    while (!connect.IsCompleted && !child.HasExited && waited < 60000) { Thread.Sleep(100); waited += 100; }
                    if (!connect.IsCompleted)
                        return new Outcome { Message = child.HasExited ? "The administrator part stopped before it started (exit code " + child.ExitCode + ")." : "The administrator part didn't connect within a minute." };
                    connect.GetAwaiter().GetResult();

                    // The other end must be the process we just started, not anything that guessed the name.
                    if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var clientPid) || clientPid != (uint)child.Id)
                        return new Outcome { Message = "Something other than Setup's own administrator part connected, so it was ignored." };

                    var outcome = new Outcome { Message = "The administrator part ended without a result." };
                    using (var reader = new StreamReader(server))
                    {
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (!SetupMessage.TryParse(line, out var kind, out var percent, out var text)) continue;
                            if (kind == "PROGRESS") progress(percent, text);
                            else if (kind == "LOG") log(text);
                            else if (kind == "DONE")
                            {
                                outcome.Ok = percent == 1;
                                outcome.Message = text;
                            }
                        }
                    }
                    child.WaitForExit(5000);
                    return outcome;
                }
            }
        }

        // ---------- the admin half itself ----------

        public static int RunElevatedHost(SetupCommandLine cmd)
        {
            using (var client = new NamedPipeClientStream(".", cmd.PipeName!, PipeDirection.Out))
            {
                client.Connect(15000);
                using (var writer = new StreamWriter(client) { AutoFlush = true })
                {
                    void Send(string line) { try { writer.WriteLine(line); } catch (IOException) { /* the wizard closed */ } }
                    var layout = new InstallLayout(); // never a sandbox: that option isn't honoured here
                    try
                    {
                        var manifest = Embedded.Manifest() ?? new PayloadManifest();
                        var engine = new InstallEngine(layout, manifest, Embedded.OpenPayload, new WindowsOps(layout));
                        InstallResult result;
                        if (cmd.Action == "uninstall")
                            result = engine.Uninstall(cmd.RemoveMachineData, (p, t) => Send(SetupMessage.Progress(p, t)), t => Send(SetupMessage.Log(t)));
                        else
                        {
                            if (!Embedded.HasPayload) { Send(SetupMessage.Done(false, "This Setup has no app inside it.")); return 1; }
                            result = engine.Install((p, t) => Send(SetupMessage.Progress(p, t)), t => Send(SetupMessage.Log(t)));
                        }
                        Send(SetupMessage.Done(result.Ok, result.Message));
                        return result.Ok ? 0 : 1;
                    }
                    catch (Exception ex)
                    {
                        Send(SetupMessage.Log(ex.ToString()));
                        Send(SetupMessage.Done(false, ex.Message));
                        return 1;
                    }
                }
            }
        }
    }
}
