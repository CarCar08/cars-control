using System;
using System.Collections.Generic;

namespace CarsControl.Setup
{
    public enum SetupMode
    {
        /// <summary>Show the wizard (install, or update/repair/uninstall when already installed).</summary>
        Interactive,
        Install,
        Update,
        Repair,
        Uninstall,
    }

    /// <summary>
    /// Setup's command line:
    ///   CarsControl-Setup.exe                          wizard
    ///   --install | --update | --repair | --uninstall  same, from a script or the app's self-update
    ///   --silent                                       no wizard window (progress on the console log only)
    ///   --keep-data / --remove-data                    for --uninstall: what happens to %LOCALAPPDATA%\CarsControl (--remove-data also
    ///                                                  removes the helper's ProgramData folder; --remove-machine-data only that)
    ///   --launch                                       start the app when done (default for the wizard and --update)
    ///   --sandbox "folder"                             test mode: everything into that folder, no admin, no registry
    ///   --elevated --pipe NAME --action install|uninstall    (internal) the admin half, started by the user half
    /// </summary>
    public sealed class SetupCommandLine
    {
        public SetupMode Mode { get; private set; } = SetupMode.Interactive;
        public bool Silent { get; private set; }
        public bool Elevated { get; private set; }
        public string? PipeName { get; private set; }
        public string? Action { get; private set; }
        public bool? RemoveData { get; private set; }

        /// <summary>For --uninstall: also delete the admin helper's logs and quarantine (C:\ProgramData\CarsControl).</summary>
        public bool RemoveMachineData { get; private set; }
        public bool Launch { get; private set; }
        public string? Sandbox { get; private set; }
        public string? WaitForPid { get; private set; }
        public List<string> Problems { get; } = new List<string>();

        public static SetupCommandLine Parse(IEnumerable<string> args)
        {
            var c = new SetupCommandLine();
            var list = new List<string>(args);
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                string? Next()
                {
                    if (i + 1 < list.Count) return list[++i];
                    c.Problems.Add("Missing value after " + a);
                    return null;
                }

                switch (a.ToLowerInvariant())
                {
                    case "--install": c.Mode = SetupMode.Install; break;
                    case "--update": c.Mode = SetupMode.Update; c.Launch = true; break;
                    case "--repair": c.Mode = SetupMode.Repair; break;
                    case "--uninstall": c.Mode = SetupMode.Uninstall; break;
                    case "--silent": c.Silent = true; break;
                    case "--launch": c.Launch = true; break;
                    case "--keep-data": c.RemoveData = false; break;
                    case "--remove-data": c.RemoveData = true; c.RemoveMachineData = true; break;
                    case "--remove-machine-data": c.RemoveMachineData = true; break;
                    case "--elevated": c.Elevated = true; break;
                    case "--pipe": c.PipeName = Next(); break;
                    case "--action": c.Action = Next(); break;
                    case "--sandbox": c.Sandbox = Next(); break;
                    case "--wait-for-pid": c.WaitForPid = Next(); break;
                    default: c.Problems.Add("Unknown option: " + a); break;
                }
            }
            if (c.Elevated && (c.PipeName == null || !IsValidPipeName(c.PipeName))) c.Problems.Add("The elevated half needs a valid --pipe name.");
            if (c.Elevated && c.Action != "install" && c.Action != "uninstall") c.Problems.Add("The elevated half needs --action install or uninstall.");
            return c;
        }

        /// <summary>CarsSetup.{32 hex}: only names the user half generated are accepted.</summary>
        public static bool IsValidPipeName(string name)
        {
            const string prefix = "CarsSetup.";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length != prefix.Length + 32) return false;
            for (var i = prefix.Length; i < name.Length; i++)
                if (!Uri.IsHexDigit(name[i])) return false;
            return true;
        }

        public static string NewPipeName() => "CarsSetup." + Guid.NewGuid().ToString("N");
    }

    /// <summary>One line of the conversation between the wizard (as you) and its admin half over a pipe:
    /// PROGRESS|40|Copying files, LOG|text, DONE|ok|message, DONE|fail|message.</summary>
    public static class SetupMessage
    {
        public static string Progress(int percent, string text) => "PROGRESS|" + Math.Max(0, Math.Min(100, percent)) + "|" + Clean(text);
        public static string Log(string text) => "LOG|" + Clean(text);
        public static string Done(bool ok, string message) => "DONE|" + (ok ? "ok" : "fail") + "|" + Clean(message);

        private static string Clean(string s) => s.Replace("\r", " ").Replace("\n", " ");

        public static bool TryParse(string line, out string kind, out int percent, out string text)
        {
            kind = "";
            percent = 0;
            text = "";
            var parts = line.Split(new[] { '|' }, 3);
            if (parts.Length < 2) return false;
            kind = parts[0];
            switch (kind)
            {
                case "PROGRESS":
                    if (parts.Length < 3 || !int.TryParse(parts[1], out percent)) return false;
                    text = parts[2];
                    return true;
                case "LOG":
                    text = parts[1] + (parts.Length > 2 ? "|" + parts[2] : "");
                    return true;
                case "DONE":
                    percent = parts[1] == "ok" ? 1 : 0;
                    text = parts.Length > 2 ? parts[2] : "";
                    return true;
                default:
                    return false;
            }
        }
    }
}
