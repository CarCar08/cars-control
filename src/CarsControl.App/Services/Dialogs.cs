using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using TextBlock = System.Windows.Controls.TextBlock;

namespace CarsControl.App.Services;

/// <summary>
/// Themed dialogs for view models (the old System.Windows.MessageBox was a white Windows 7-style box in a
/// dark app). View models call these instead of any UI type, so a future macOS UI only needs its own
/// implementation of this class.
/// </summary>
public static class Dialogs
{
    public const string AppTitle = "C.A.R.S. Control";

    public static async Task InfoAsync(string message, string title = AppTitle)
    {
        await Build(title, message, primary: null, close: "OK").ShowDialogAsync(showAsDialog: !App.IsDevInstance);
    }

    /// <summary>True when the confirm button was pressed. <paramref name="danger"/> colors it as destructive.</summary>
    public static async Task<bool> ConfirmAsync(string message, string confirm = "OK", string cancel = "Cancel", string title = AppTitle, bool danger = false)
    {
        var box = Build(title, message, confirm, cancel);
        if (danger) box.PrimaryButtonAppearance = ControlAppearance.Danger;
        return await box.ShowDialogAsync(showAsDialog: !App.IsDevInstance) == MessageBoxResult.Primary;
    }

    /// <summary>Lists apps that are open and would be closed by their update, and asks once for the batch.</summary>
    public static async Task<OpenAppsChoice> AskAboutOpenAppsAsync(IEnumerable<string> names)
    {
        var box = Build("Some apps are open",
            $"These apps are running and need to close to update:\n\n{string.Join(", ", names)}\n\n" +
            "Close them now? (They'll be asked to close normally — unsaved work prompts still appear.)",
            "Close them and update", "Cancel");
        box.SecondaryButtonText = "Skip those apps";
        box.IsSecondaryButtonEnabled = true;
        return await box.ShowDialogAsync(showAsDialog: !App.IsDevInstance) switch
        {
            MessageBoxResult.Primary => OpenAppsChoice.CloseThem,
            MessageBoxResult.Secondary => OpenAppsChoice.SkipThem,
            _ => OpenAppsChoice.Cancel,
        };
    }

    /// <summary>A (?) explainer: four short headed sections.</summary>
    public static async Task HelpAsync(CarsControl.Core.Help.HelpTopic topic)
    {
        topic = topic.ForPc(Extras.Salad);
        var panel = new StackPanel { MaxWidth = 520 };
        void Section(string heading, string text)
        {
            panel.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 12, 0, 2) });
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        }
        Section("What it is", topic.What);
        Section("What C.A.R.S. does", topic.Does);
        Section("What you should do", topic.ToDo);
        Section("Is it safe?", topic.Safety);
        var box = Build(topic.Title, "", primary: null, close: "Got it");
        box.Content = new ScrollViewer { Content = panel, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        await box.ShowDialogAsync(showAsDialog: !App.IsDevInstance);
    }

    private static MessageBox Build(string title, string message, string? primary, string close)
    {
        var box = new MessageBox
        {
            Title = title,
            // Wrapping text block: long paths used to stretch the box across the screen.
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            CloseButtonText = close,
            IsPrimaryButtonEnabled = primary is not null,
            PrimaryButtonText = primary ?? "",
            MaxWidth = 600,
        };
        if (Application.Current?.MainWindow is { IsVisible: true } owner) box.Owner = owner;
        // Test copies run while you use the PC: their dialogs must not take focus from what you're doing.
        if (App.IsDevInstance) { box.ShowActivated = false; box.SourceInitialized += (_, _) => DevFocusGuard.NoActivate(box); }
        return box;
    }
}
