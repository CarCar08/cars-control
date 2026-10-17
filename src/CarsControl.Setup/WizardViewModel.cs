using System;
using System.ComponentModel;
using System.Windows.Controls;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CarsControl.Setup;

namespace CarsControl.SetupApp
{
    internal enum Stage { Welcome, ConfirmUninstall, Working, Done }

    internal sealed class RelayCommand : ICommand
    {
        private readonly Action _run;
        private readonly Func<bool>? _can;

        public RelayCommand(Action run, Func<bool>? can = null)
        {
            _run = run;
            _can = can;
        }

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => _can?.Invoke() ?? true;
        public void Execute(object? parameter) => _run();
        public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What the wizard window shows. The words live here, so they're easy to find and to review.</summary>
    internal sealed class WizardViewModel : INotifyPropertyChanged
    {
        private readonly SetupController _setup;
        private readonly Func<bool> _canClose;
        private readonly StringBuilder _log = new StringBuilder();
        private Intent _intent;
        private bool _ok;

        public event PropertyChangedEventHandler? PropertyChanged;

        public WizardViewModel(SetupController setup, Action requestClose, Func<bool> canClose)
        {
            _setup = setup;
            _canClose = canClose;
            _setup.Progress += (p, t) => Ui(() => { Percent = p; Step = t; });
            _setup.LogLine += t => Ui(() => { _log.AppendLine(t); Raise(nameof(LogText)); });
            var installed = setup.Installed();
            StartWithWindows = installed.IsInstalled ? setup.StartWithWindowsNow() : true;
            LaunchWhenDone = true;
            RemoveUserData = false;
            RemoveMachineData = false;

            Primary = new RelayCommand(() => _ = RunPrimaryAsync(), () => PrimaryEnabled);
            Secondary = new RelayCommand(() => _ = RunSecondaryAsync());
            CloseCommand = new RelayCommand(requestClose, () => _canClose());
            LaunchCommand = new RelayCommand(() => { _setup.Launch(); requestClose(); });
            ShowLogCommand = new RelayCommand(() => { try { Process.Start(new ProcessStartInfo(_setup.LogFile) { UseShellExecute = true })?.Dispose(); } catch (Win32Exception) { } });
            CopyDetailsCommand = new RelayCommand(() => { try { Clipboard.SetText(_log.ToString()); CopyHint = "Copied."; Raise(nameof(CopyHint)); } catch (System.Runtime.InteropServices.ExternalException) { } });
            ShowIntro(installed, Intent.Install);
        }

        public ICommand Primary { get; }
        public ICommand Secondary { get; }
        public ICommand CloseCommand { get; }
        public ICommand LaunchCommand { get; }
        public ICommand ShowLogCommand { get; }
        public ICommand CopyDetailsCommand { get; }
        public string? CopyHint { get; private set; }

        // ---------- what's on screen ----------

        private Stage _stage;
        public Stage Stage
        {
            get => _stage;
            private set { _stage = value; RaiseAll(); }
        }

        public bool IsWelcome => Stage == Stage.Welcome;
        public bool IsConfirm => Stage == Stage.ConfirmUninstall;
        public bool IsWorking => Stage == Stage.Working;
        public bool IsDone => Stage == Stage.Done;
        public bool ShowFooterButtons => Stage != Stage.Working;

        public string Headline { get; private set; } = "";
        public string Subline { get; private set; } = "";
        public string Body { get; private set; } = "";
        public string Fine { get; private set; } = "";
        public string PrimaryText { get; private set; } = "Install";
        public bool PrimaryEnabled { get; private set; } = true;
        public string? SecondaryText { get; private set; }
        public bool HasSecondary => SecondaryText != null;
        public bool HasSubline => !string.IsNullOrEmpty(Subline);
        public bool HasSecondaryOnWelcome => IsWelcome && HasSecondary;
        public bool ShowPrimary => !IsDone;
        public string CloseText { get; private set; } = "Cancel";
        public bool ShowStartOption { get; private set; } = true;
        public bool ShowLaunchOption { get; private set; } = true;
        public string StartOptionText => "Start C.A.R.S. Control when I sign in (quietly, in the tray)";

        private bool _startWithWindows, _launchWhenDone, _removeUser, _removeMachine;
        public bool StartWithWindows { get => _startWithWindows; set { _startWithWindows = value; Raise(); } }
        public bool LaunchWhenDone { get => _launchWhenDone; set { _launchWhenDone = value; Raise(); } }
        public bool RemoveUserData { get => _removeUser; set { _removeUser = value; Raise(); } }
        public bool RemoveMachineData { get => _removeMachine; set { _removeMachine = value; Raise(); } }

        private int _percent;
        public int Percent { get => _percent; private set { _percent = value; Raise(); } }
        private string _step = "";
        public string Step { get => _step; private set { _step = value; Raise(); } }
        public string LogText => _log.ToString();

        public bool ResultOk => _ok;
        public string ResultTitle { get; private set; } = "";
        public string ResultText { get; private set; } = "";
        public bool ShowLaunchButton { get; private set; }

        // ---------- stages ----------

        private void ShowIntro(InstalledInfo installed, Intent preferred)
        {
            var mine = _setup.ThisVersion;
            if (!_setup.HasPayload && !_setup.Layout.IsSandbox && !installed.IsInstalled)
            {
                _intent = Intent.Install;
                Headline = "This Setup file is empty";
                Subline = "It was built without the app inside it";
                Body = "Download CarsControl-Setup from the project's Releases page (github.com/CarCar08/cars-control/releases) and run that one.";
                PrimaryText = "Install";
                PrimaryEnabled = false;
                SecondaryText = null;
                CloseText = "Close";
                ShowStartOption = ShowLaunchOption = false;
                Stage = Stage.Welcome;
                return;
            }

            if (!installed.IsInstalled)
            {
                _intent = Intent.Install;
                Headline = "Install C.A.R.S. Control";
                Subline = "Version " + mine;
                Body = "One app to see, update, uninstall and clean up everything on your PC: your apps and games, Windows Update and winget, disk space, privacy switches and more.\n\n" +
                       "It installs for everyone on this PC into " + (Environment.GetEnvironmentVariable("CARS_DOC_SHOT") == "1" ? @"C:\Program Files\CarsControl" : _setup.Layout.InstallDir) + " and adds a Start menu shortcut. Windows asks for an administrator's approval once.";
                Fine = DotNetRuntime.IsSatisfied(_setup.Manifest?.RequiredRuntime ?? "10.0")
                    ? "Nothing runs in the background until you start the app. Setup adds no services or scheduled tasks."
                    : "Your PC doesn't have Microsoft's .NET " + (_setup.Manifest?.RequiredRuntime ?? "10.0") + " Desktop Runtime yet: Setup downloads it from Microsoft (about 60 MB) and checks its checksum.";
                PrimaryText = "Install";
                SecondaryText = null;
                CloseText = "Cancel";
                ShowStartOption = ShowLaunchOption = true;
            }
            else if (installed.Version!.CompareTo(mine) < 0)
            {
                _intent = Intent.Update;
                Headline = "Update C.A.R.S. Control";
                Subline = installed.Version + "  →  " + mine;
                Body = "Your settings, history, scans and ignore rules are kept. The app closes for a moment while its files are replaced, and opens again afterwards.";
                Fine = "If anything goes wrong the version you have now is put back.";
                PrimaryText = "Update";
                SecondaryText = "Uninstall…";
                CloseText = "Cancel";
                ShowStartOption = true;
                ShowLaunchOption = true;
            }
            else if (installed.Version.CompareTo(mine) == 0)
            {
                _intent = Intent.Repair;
                Headline = "C.A.R.S. Control " + mine + " is installed";
                Subline = "Installed in " + (installed.Location ?? _setup.Layout.InstallDir);
                Body = "Repair puts every file back exactly as shipped (your settings and data aren't touched). Uninstall removes the program.";
                PrimaryText = "Repair";
                SecondaryText = "Uninstall…";
                CloseText = "Close";
                ShowStartOption = true;
                ShowLaunchOption = true;
            }
            else
            {
                _intent = Intent.Reinstall;
                Headline = "A newer C.A.R.S. Control is installed";
                Subline = installed.Version + " is installed; this Setup has " + mine;
                Body = "Installing this older version replaces it. Your settings and data are kept, but a newer version may have changed them in ways an older one doesn't understand.";
                PrimaryText = "Install " + mine + " anyway";
                SecondaryText = "Uninstall…";
                CloseText = "Close";
                ShowStartOption = true;
                ShowLaunchOption = true;
            }
            PrimaryEnabled = true;
            Stage = Stage.Welcome;
            RaiseAll();
        }

        public void StartAutomatically(SetupMode mode)
        {
            switch (mode)
            {
                case SetupMode.Install:
                case SetupMode.Update:
                case SetupMode.Repair:
                    _ = RunAsync(_setup.Installed().IsInstalled ? (mode == SetupMode.Repair ? Intent.Repair : Intent.Update) : Intent.Install);
                    break;
                case SetupMode.Uninstall:
                    ShowUninstallConfirm();
                    break;
            }
        }

        private void ShowUninstallConfirm()
        {
            Headline = "Uninstall C.A.R.S. Control";
            Subline = "";
            Body = "This removes the program, its Start menu shortcut and its entry in Apps. What you've chosen in the app (privacy switches, hidden startup items…) stays as it is: switch those back in the app first if you want Windows' defaults.";
            Fine = "Your settings, history and scans stay in " + InstallLayout.UserData + " unless you tick the box below.";
            PrimaryText = "Uninstall";
            SecondaryText = null;
            CloseText = "Cancel";
            PrimaryEnabled = true;
            _intent = Intent.Uninstall;
            Stage = Stage.ConfirmUninstall;
            RaiseAll();
        }

        private async Task RunPrimaryAsync()
        {
            if (Stage == Stage.ConfirmUninstall) await RunAsync(Intent.Uninstall);
            else await RunAsync(_intent);
        }

        private Task RunSecondaryAsync()
        {
            ShowUninstallConfirm();
            return Task.CompletedTask;
        }

        private async Task RunAsync(Intent intent)
        {
            _intent = intent;
            Stage = Stage.Working;
            Percent = 0;
            Step = "Starting";
            RaiseAll();
            InstallResult result;
            try
            {
                result = await _setup.RunAsync(intent, new Options
                {
                    StartWithWindows = StartWithWindows,
                    LaunchWhenDone = LaunchWhenDone,
                    RemoveUserData = RemoveUserData,
                    RemoveMachineData = RemoveMachineData,
                });
            }
            catch (Exception ex)
            {
                // Never leave the window on a step that stopped: this runs fire-and-forget, so an error here would vanish and the
                // wizard would refuse to close because it still thinks it's working.
                result = new InstallResult { Ok = false, Message = "Setup hit an unexpected problem: " + ex.Message + " Nothing after that step was done; you can run Setup again." };
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CarsControl-Setup.log"), "FAILED: " + ex + Environment.NewLine); }
                catch (System.IO.IOException) { }
            }
            _ok = result.Ok;
            if (result.Ok)
            {
                ResultTitle = intent == Intent.Uninstall ? "C.A.R.S. Control was removed"
                    : intent == Intent.Update ? "Updated to " + _setup.ThisVersion
                    : intent == Intent.Repair ? "Repaired"
                    : "C.A.R.S. Control is installed";
                ResultText = intent == Intent.Uninstall ? "Thanks for trying it."
                    : LaunchWhenDone ? "It's opening now. You'll find it in the Start menu as \"C.A.R.S. Control\"."
                    : "Find it in the Start menu as \"C.A.R.S. Control\".";
                ShowLaunchButton = intent != Intent.Uninstall && !LaunchWhenDone;
                CloseText = "Close";
            }
            else
            {
                ResultTitle = "Setup couldn't finish";
                ResultText = result.Message;
                ShowLaunchButton = false;
                CloseText = "Close";
            }
            Stage = Stage.Done;
            RaiseAll();
        }

        // ---------- plumbing ----------

        private static void Ui(Action a)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) a(); else d.BeginInvoke(a);
        }

        private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
