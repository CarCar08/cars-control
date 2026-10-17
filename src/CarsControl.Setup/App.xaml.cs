using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CarsControl.Setup;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace CarsControl.SetupApp
{
    public partial class App : Application
    {
        static App() => Embedded.Hook(); // before anything else needs an embedded library

        /// <summary>An unexpected error is written to the log and shown, instead of Windows' generic crash box.</summary>
        private static void Crash(Exception? ex, bool show)
        {
            var text = "Setup hit an unexpected problem: " + (ex?.ToString() ?? "unknown");
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "CarsControl-Setup.log"), text + Environment.NewLine); } catch (IOException) { }
            if (show)
                System.Windows.MessageBox.Show("Setup hit an unexpected problem and had to stop:\n\n" + ex?.Message + "\n\nDetails are in " + Path.Combine(Path.GetTempPath(), "CarsControl-Setup.log"),
                    "C.A.R.S. Control Setup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, args) =>
            {
                args.Handled = true;
                Crash(args.Exception, show: true);
                Shutdown(3);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) => Crash(args.ExceptionObject as Exception, show: false);
            base.OnStartup(e);
            // Windows won't rename a folder that a running program has as its current directory. Setup used to inherit the app's
            // (C:\Program Files\CarsControl when started from the Start menu) and then couldn't swap that very folder (0.9.0-rc.6).
            try { Directory.SetCurrentDirectory(Path.GetTempPath()); } catch (IOException) { }
            var cmd = SetupCommandLine.Parse(e.Args);

            // The administrator half: no window, just the work and its progress over the pipe.
            if (cmd.Elevated)
            {
                if (cmd.Problems.Count > 0) { Shutdown(2); return; }
                Shutdown(Elevation.RunElevatedHost(cmd));
                return;
            }
            if (cmd.Problems.Count > 0)
            {
                System.Windows.MessageBox.Show(string.Join(Environment.NewLine, cmd.Problems) + Environment.NewLine + Environment.NewLine +
                                               "Options: --install --update --repair --uninstall --silent --keep-data --remove-data --launch",
                                               "C.A.R.S. Control Setup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                Shutdown(2);
                return;
            }

            var setup = new SetupController(cmd);

            // Setup can't delete or swap the folder it's running from (the installed CarsControl-Setup.exe, used by Apps' Uninstall
            // and Modify): continue from a copy in Temp, whatever it was asked to do.
            if (cmd.Sandbox == null)
            {
                var self = Process.GetCurrentProcess().MainModule!.FileName;
                if (self.StartsWith(setup.Layout.InstallDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    var temp = Path.Combine(Path.GetTempPath(), "CarsControl-Uninstall-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temp);
                    var copy = Path.Combine(temp, "CarsControl-Setup.exe");
                    File.Copy(self, copy);
                    Process.Start(new ProcessStartInfo(copy, string.Join(" ", e.Args.Select(a => a.Contains(" ") ? "\"" + a + "\"" : a))) { UseShellExecute = false })?.Dispose();
                    Shutdown(0);
                    return;
                }
            }

            if (cmd.Silent)
            {
                Shutdown(RunSilently(setup, cmd));
                return;
            }

            ApplyTheme();
            var window = new WizardWindow(setup, cmd.Mode);
            MainWindow = window;
            // Exit when the wizard closes, whatever else exists: with the default "last window closed" rule, a hidden helper
            // window kept 0.9.0-rc.1's Setup running after an update, which locked its file and blocked the next update.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closed += (_, _) => Shutdown(0);
            window.Show();
        }

        private static int RunSilently(SetupController setup, SetupCommandLine cmd)
        {
            var installed = setup.Installed();
            var intent = cmd.Mode == SetupMode.Uninstall ? Intent.Uninstall
                : cmd.Mode == SetupMode.Repair ? Intent.Repair
                : installed.IsInstalled ? Intent.Update : Intent.Install;
            var options = new Options
            {
                StartWithWindows = installed.IsInstalled && setup.StartWithWindowsNow(),
                LaunchWhenDone = cmd.Launch,
                RemoveUserData = cmd.RemoveData == true,
                RemoveMachineData = cmd.RemoveMachineData,
            };
            var result = setup.RunAsync(intent, options).GetAwaiter().GetResult();
            return result.Ok ? 0 : 1;
        }

        /// <summary>Follows Windows' light or dark setting, with the app's teal accent. The accent is applied
        /// before and after the theme: reloading a theme copies whichever accent is current at that moment.</summary>
        private static void ApplyTheme()
        {
            var theme = ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
            var teal = (Color)ColorConverter.ConvertFromString("#18A08C");
            ApplicationAccentColorManager.Apply(teal, theme);
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);
            ApplicationAccentColorManager.Apply(teal, theme);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // A copy of Setup that ran the uninstall from Temp tidies itself up once it has exited.
            try
            {
                var self = Process.GetCurrentProcess().MainModule!.FileName;
                var dir = Path.GetDirectoryName(self)!;
                if (Path.GetFileName(dir).StartsWith("CarsControl-Uninstall-", StringComparison.Ordinal) && dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"" + dir + "\"") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception) { }
            base.OnExit(e);
        }
    }
}
