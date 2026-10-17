using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace CarsControl.App.Controls;

/// <summary>
/// Banner behavior for <see cref="InfoBar"/>: <c>Notice.AutoDismiss="True"</c> closes a success message by itself after a
/// few seconds (warnings and errors stay until you close them). Closing goes through IsOpen, so the two-way binding to the
/// view model's message clears it, exactly like pressing ✕.
/// </summary>
public static class Notice
{
    public static readonly TimeSpan SuccessLifetime = TimeSpan.FromSeconds(8);

    public static readonly DependencyProperty AutoDismissProperty = DependencyProperty.RegisterAttached(
        "AutoDismiss", typeof(bool), typeof(Notice), new PropertyMetadata(false, OnAutoDismissChanged));

    private static readonly DependencyProperty TimerProperty = DependencyProperty.RegisterAttached(
        "Timer", typeof(DispatcherTimer), typeof(Notice));

    public static bool GetAutoDismiss(DependencyObject o) => (bool)o.GetValue(AutoDismissProperty);

    public static void SetAutoDismiss(DependencyObject o, bool value) => o.SetValue(AutoDismissProperty, value);

    private static readonly DependencyPropertyDescriptor MessageDescriptor = DependencyPropertyDescriptor.FromProperty(InfoBar.MessageProperty, typeof(InfoBar));
    private static readonly DependencyPropertyDescriptor SeverityDescriptor = DependencyPropertyDescriptor.FromProperty(InfoBar.SeverityProperty, typeof(InfoBar));

    private static void OnAutoDismissChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not InfoBar bar || e.NewValue is not true) return;
        // Watched only while the banner is loaded: AddValueChanged is a strong, app-wide reference, and a banner watched
        // forever kept its whole page alive after the page was released (found with dotnet-dump gcroot).
        EventHandler changed = (_, _) => Restart(bar);
        var watching = false;
        void Watch()
        {
            if (watching) return; // WPF can raise Loaded twice in a row
            watching = true;
            MessageDescriptor.AddValueChanged(bar, changed);
            SeverityDescriptor.AddValueChanged(bar, changed);
        }
        bar.Loaded += (_, _) => { Watch(); Restart(bar); };
        bar.Unloaded += (_, _) =>
        {
            if (!watching) return;
            watching = false;
            MessageDescriptor.RemoveValueChanged(bar, changed);
            SeverityDescriptor.RemoveValueChanged(bar, changed);
            (bar.GetValue(TimerProperty) as DispatcherTimer)?.Stop();
        };
        if (bar.IsLoaded) { Watch(); Restart(bar); }
    }

    private static void Restart(InfoBar bar)
    {
        if (bar.GetValue(TimerProperty) is not DispatcherTimer timer)
        {
            timer = new DispatcherTimer { Interval = SuccessLifetime };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (bar.Severity == InfoBarSeverity.Success) bar.SetCurrentValue(InfoBar.IsOpenProperty, false);
            };
            bar.SetValue(TimerProperty, timer);
        }
        timer.Stop();
        if (bar.Severity == InfoBarSeverity.Success && !string.IsNullOrEmpty(bar.Message)) timer.Start();
    }
}
