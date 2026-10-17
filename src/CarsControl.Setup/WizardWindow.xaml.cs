using System.ComponentModel;
using System.Windows.Controls;
using CarsControl.App.Services;
using CarsControl.Setup;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace CarsControl.SetupApp
{
    public partial class WizardWindow : FluentWindow
    {
        private readonly WizardViewModel _vm;

        internal WizardWindow(SetupController setup, SetupMode autoStart)
        {
            InitializeComponent();
            _vm = new WizardViewModel(setup, Close, () => !_vm!.IsWorking);
            DataContext = _vm;
            // updateAccents: false, or the watcher puts Windows' accent (red here) over the app's teal.
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: false);

            // Test runs (CARS_DEV_INSTANCE=1) open on the TV and never take focus, like the app's test copies.
            if (System.Environment.GetEnvironmentVariable("CARS_DEV_INSTANCE") == "1")
            {
                ShowActivated = false;
                if (DevWindowPlacement.InitialPosition() is { } where)
                {
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                    Left = where.X;
                    Top = where.Y;
                }
                SourceInitialized += (_, _) => DevWindowPlacement.MoveToSecondaryMonitor(this);
            }

            if (autoStart != SetupMode.Interactive) Loaded += (_, _) => _vm.StartAutomatically(autoStart);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Closing half way through a copy would leave a half-installed app; the engine rolls back on failure,
            // but a killed process can't.
            if (_vm.IsWorking) e.Cancel = true;
            base.OnClosing(e);
        }

        private void OnLogChanged(object sender, TextChangedEventArgs e) => ((System.Windows.Controls.TextBox)sender).ScrollToEnd();
    }
}
