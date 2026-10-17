using System.Windows;
using System.Windows.Automation;
using CarsControl.App.Services;
using CarsControl.Core.Help;
using Wpf.Ui.Controls;

namespace CarsControl.App.Controls;

/// <summary>
/// The small (?) next to a section title: <c>&lt;ctl:HelpButton Topic="health-drives" /&gt;</c>. Hovering shows the one-line
/// "what it is"; clicking opens the full explainer (what it is, what C.A.R.S. does, what to do, is it safe). Topics live in
/// <see cref="HelpTopics"/>.
/// </summary>
public sealed class HelpButton : Button
{
    public static readonly DependencyProperty TopicProperty = DependencyProperty.Register(
        nameof(Topic), typeof(string), typeof(HelpButton), new PropertyMetadata(null, (d, _) => ((HelpButton)d).Describe()));

    public string? Topic
    {
        get => (string?)GetValue(TopicProperty);
        set => SetValue(TopicProperty, value);
    }

    public HelpButton()
    {
        // A subclass doesn't get WPF-UI's implicit Button style (implicit styles are keyed by the exact type): use it explicitly.
        SetResourceReference(StyleProperty, typeof(Button));
        Appearance = ControlAppearance.Transparent;
        // Accent-coloured so it reads as clickable: inheriting the surrounding text colour made it nearly invisible on cards.
        var icon = new SymbolIcon { Symbol = SymbolRegular.QuestionCircle24, FontSize = 18 };
        icon.SetResourceReference(SymbolIcon.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        Content = icon;
        Padding = new Thickness(4);
        Margin = new Thickness(6, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Focusable = true;
        Click += async (_, _) =>
        {
            if (HelpTopics.Find(Topic) is { } t) await Dialogs.HelpAsync(t);
        };
    }

    private void Describe()
    {
        var t = HelpTopics.Find(Topic)?.ForPc(Services.Extras.Salad);
        ToolTip = t is null ? null : $"{t.What}\n\nClick for more: what C.A.R.S. does, what to do, is it safe.";
        AutomationProperties.SetName(this, t is null ? "Help" : $"What is {t.Title}?");
    }
}
