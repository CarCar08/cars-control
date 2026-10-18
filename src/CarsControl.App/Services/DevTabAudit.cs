using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CarsControl.App.Services;

/// <summary>
/// Test copies only (CARS_DEV_INSTANCE=1 and `--page tabaudit`): walks the keyboard focus order of every page
/// with WPF's own Tab traversal (no real key presses, so the window never has to take focus from
/// whatever you're doing) and writes %TEMP%\CarsControl\tab-order.txt. It flags:
///   NO NAME     a Tab stop that a screen reader would announce as just "button"
///   OFFSCREEN   a Tab stop that sits outside the visible window (nothing scrolls it into view)
///   TRAP        the walk never returned to its start within 400 stops
///   NO STOPS    a page you can't reach with the keyboard at all
/// </summary>
internal static partial class DevTabAudit
{
    /// <summary>Text that's a .NET type name ("System.Windows.Controls.TextBlock", "CarsControl.App.ViewModels.Row"), not a file name.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(System\.Windows|CarsControl\.(App|Core|Contracts))(\.[A-Z]\w*)+$")]
    private static partial System.Text.RegularExpressions.Regex TypeName();

    public static bool Enabled => App.IsDevInstance;

    public static async Task RunAsync(MainWindow window)
    {
        var report = new StringBuilder();
        var flags = 0;
        var helpFlags = 0;
        (string Name, Type Page)[] pages =
        [
            ("Home", typeof(Views.HomePage)), ("Apps", typeof(Views.AppsPage)), ("Refresh", typeof(Views.RefreshPage)),
            ("Storage", typeof(Views.StoragePage)), ("Cleanup", typeof(Views.CleanupPage)), ("Health", typeof(Views.HealthPage)), ("Privacy", typeof(Views.PrivacyPage)), ("Optimization", typeof(Views.OptimizePage)), ("Backup & sync", typeof(Views.BackupPage)),
            ("History", typeof(Views.HistoryPage)), ("Settings", typeof(Views.SettingsPage)),
        ];
        foreach (var (name, page) in pages)
        {
            window.NavigateTo(page);
            await Task.Delay(1500);
            // Pages with tabs: walk every tab, not only the one that's showing.
            if (Elements(window).OfType<ListBox>().FirstOrDefault(l => l.Name == "SectionList" && l.IsVisible) is { } sections)
            {
                foreach (var section in sections.Items.OfType<ListBoxItem>())
                {
                    sections.SelectedItem = section;
                    await Task.Delay(250);
                    flags += await WalkAsync(window, $"{name} > {AutomationProperties.GetName(section)}", report);
                    helpFlags += CheckExplanations(window, report);
                }
            }
            else if (FindTabs(window) is { Items.Count: > 1 } tabs)
            {
                for (var i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    await Task.Delay(1800);
                    var header = (tabs.Items[i] as TabItem)?.Header?.ToString() ?? $"tab {i + 1}";
                    flags += await WalkAsync(window, $"{name} › {header}", report);
                    helpFlags += CheckExplanations(window, report);
                }
            }
            else
            {
                flags += await WalkAsync(window, name, report);
                helpFlags += CheckExplanations(window, report);
            }
        }
        var settingsVm = App.Services.GetService(typeof(ViewModels.SettingsViewModel)) as ViewModels.SettingsViewModel;
        if (settingsVm is not null)
        {
            window.NavigateTo(typeof(Views.SettingsPage));
            await Task.Delay(200);
            var previous = settingsVm.Message;
            settingsVm.Message = "UI audit: close this notice";
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var banner = Elements(window).OfType<Wpf.Ui.Controls.InfoBar>().FirstOrDefault(b => b.Message?.ToString() == settingsVm.Message);
            if (banner is null) { report.AppendLine("  BANNER: test notice wasn't shown"); flags++; }
            else
            {
                banner.SetCurrentValue(Wpf.Ui.Controls.InfoBar.IsOpenProperty, false);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                if (settingsVm.Message is not null) { report.AppendLine("  BANNER: close did not clear the bound message"); flags++; }
                else report.AppendLine("  BANNER: close cleared the bound message");
            }
            settingsVm.Message = previous;
        }
        report.AppendLine($"HELP AUDIT: {helpFlags} issue(s) (missing tips, section descriptions or clipped text)");
        report.AppendLine().AppendLine(flags == 0 ? "RESULT: clean" : $"RESULT: {flags} problem(s)");
        var dir = Path.Combine(Path.GetTempPath(), "CarsControl");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tab-order.txt"), report.ToString());
    }

    private static IEnumerable<FrameworkElement> Elements(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element) yield return element;
            foreach (var descendant in Elements(child)) yield return descendant;
        }
    }

    private static bool HasTip(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is FrameworkElement parent; current = VisualTreeHelper.GetParent(current))
            if (parent.ToolTip is not null || !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(parent))) return true;
        return false;
    }

    private static int CheckExplanations(MainWindow window, StringBuilder report)
    {
        var flags = 0;
        foreach (var element in Elements(window).Where(e => e.IsVisible && e.ActualWidth > 0 && e.ActualHeight > 0))
        {
            if (element.TemplatedParent is null && element is ButtonBase or ComboBox or TextBoxBase)
            {
                var label = AutomationProperties.GetName(element);
                if (label.Length == 0 && element is ContentControl { Content: string content }) label = content;
                if (label.Length > 0 && !HasTip(element)) { report.AppendLine($"  HELP: NO TOOLTIP {element.GetType().Name}: {label}"); flags++; }
            }
            if (element is not TextBlock { Text.Length: > 0 } text) continue;
            // An element shown through a text template, or a view model without a template, prints its type name.
            if (TypeName().IsMatch(text.Text))
            { report.AppendLine($"  HELP: TYPE NAME SHOWN {text.Text}"); flags++; }
            if (ReferenceEquals(text.Style, text.TryFindResource("SectionTitle")) && text.Parent is FrameworkElement section)
            {
                var container = section.Parent as FrameworkElement ?? section;
                if (!Elements(container).OfType<TextBlock>().Any(t => t != text && t.Text.Length > 25)
                    && !Elements(container).OfType<Controls.HelpButton>().Any())
                { report.AppendLine($"  HELP: NO DESCRIPTION {text.Text}"); flags++; }
            }
            if (text.TextTrimming != TextTrimming.None && HasTip(text)) continue;
            var measure = new TextBlock { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize,
                FontStyle = text.FontStyle, FontWeight = text.FontWeight, FontStretch = text.FontStretch,
                TextWrapping = text.TextWrapping, FlowDirection = text.FlowDirection, LineHeight = text.LineHeight,
                LineStackingStrategy = text.LineStackingStrategy, Padding = text.Padding, Language = text.Language };
            TextOptions.SetTextFormattingMode(measure, TextOptions.GetTextFormattingMode(text));
            measure.Measure(new Size(text.TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : Math.Max(1, text.ActualWidth), double.PositiveInfinity));
            if (measure.DesiredSize.Width > text.ActualWidth + 3 || measure.DesiredSize.Height > text.ActualHeight + 3)
            { report.AppendLine($"  HELP: CLIPPED TEXT {text.Text.Replace('\n', ' ')[..Math.Min(90, text.Text.Length)]}"); flags++; }
        }
        return flags;
    }

    /// <summary>The page's own TabControl (the first one in the visual tree under the navigation host).</summary>
    private static TabControl? FindTabs(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TabControl t && t.Name == "Tabs") return t;
            if (FindTabs(child) is { } found) return found;
        }
        return null;
    }

    private static async Task<int> WalkAsync(MainWindow window, string page, StringBuilder report)
    {
        report.AppendLine($"== {page} ==");
        var flags = 0;
        var seen = new HashSet<UIElement>();
        Keyboard.ClearFocus();
        UIElement current = window;
        for (var i = 0; i < 400; i++)
        {
            if (!current.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) || Keyboard.FocusedElement is not UIElement next) break;
            if (!seen.Add(next)) break; // back at a stop it already visited: the walk wrapped around
            current = next;
            // Focusing a control scrolls it into view a moment later (layout runs first): look after that.
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            report.AppendLine("  " + Describe(window, next, ref flags));
        }
        if (seen.Count >= 399) { report.AppendLine("  TRAP: focus never came back to its start"); flags++; }
        if (seen.Count == 0) { report.AppendLine("  NO STOPS: nothing on this page can be reached with Tab"); flags++; }
        report.AppendLine($"  ({seen.Count} Tab stops)");
        return flags;
    }

    private static string Describe(MainWindow window, UIElement e, ref int flags)
    {
        var name = "";
        try
        {
            name = UIElementAutomationPeer.CreatePeerForElement(e)?.GetName() ?? "";
        }
        catch (Exception)
        {
            // Not every element has a peer.
        }
        if (string.IsNullOrWhiteSpace(name) && e is FrameworkElement { Name.Length: > 0 } named) name = $"#{named.Name}";
        var type = e.GetType().Name;
        var sb = new StringBuilder($"{type,-22} {(string.IsNullOrWhiteSpace(name) ? "(no name)" : name)}");
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('#'))
        {
            // A text box or combo box inside a labelled control inherits its label; only flag controls with no text anywhere.
            if (!HasVisibleText(e)) { sb.Append("   <-- NO NAME"); flags++; }
        }
        if (e is FrameworkElement fe && fe.IsVisible)
        {
            var p = fe.TranslatePoint(new Point(0, 0), window);
            if (p.X + fe.ActualWidth < 0 || p.Y + fe.ActualHeight < 0 || p.X > window.ActualWidth || p.Y > window.ActualHeight)
            {
                sb.Append("   <-- OFFSCREEN");
                flags++;
            }
        }
        return sb.ToString();
    }

    private static bool HasVisibleText(DependencyObject root)
    {
        if (root is TextBlock { Text.Length: > 0 }) return true;
        if (root is ContentControl { Content: string { Length: > 0 } }) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (HasVisibleText(VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }
}
