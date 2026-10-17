using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CarsControl.App.Services;
using CarsControl.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CarsControl.App.Views;

public partial class SettingsPage : Page
{
    private const string LastSectionKey = "settings.section";

    public SettingsPage(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) =>
        {
            vm.Reload();
            if (SectionList.SelectedItem is null)
                Show(App.Services.GetRequiredService<AppSettings>().GetUi(LastSectionKey) ?? "Appearance");
        };
    }

    /// <summary>Opens a section ("Maintenance", "Gaming", "Alerts"…). Kept under its old name: Home, the
    /// palette and the tray call it.</summary>
    public void ScrollTo(string section) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Show(section));

    private void Show(string section)
    {
        var item = SectionList.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == section)
                   ?? SectionList.Items.OfType<ListBoxItem>().First();
        if (!ReferenceEquals(SectionList.SelectedItem, item)) SectionList.SelectedItem = item; // raises OnSectionChanged
        else ShowPanel((string)item.Tag);
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is ListBoxItem { Tag: string key }) ShowPanel(key);
    }

    private void ShowPanel(string key)
    {
        foreach (var item in SectionList.Items.OfType<ListBoxItem>())
            if (FindName("Panel" + (string)item.Tag) is FrameworkElement panel)
                panel.Visibility = (string)item.Tag == key ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ScrollToTop();
        App.Services.GetRequiredService<AppSettings>().SetUi(LastSectionKey, key);
    }

    /// <summary>The shortcut recorder: shows the combination as you press it and saves it when it's complete.</summary>
    private void OnHotkeyKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.Tab or System.Windows.Input.Key.Escape) return; // keep keyboard navigation
        e.Handled = true;
        if (GlobalHotkey.Describe(System.Windows.Input.Keyboard.Modifiers, key) is { } text && DataContext is SettingsViewModel vm)
            vm.PaletteHotkey = text;
    }

    private void OnHotkeyReset(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.PaletteHotkey = "Ctrl+Alt+Space";
    }
}
