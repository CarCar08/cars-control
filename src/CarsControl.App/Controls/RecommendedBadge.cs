using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CarsControl.App.Services;
using CarsControl.Core.Help;

namespace CarsControl.App.Controls;

/// <summary>
/// A small "Recommended" (or "Recommended: off") tag beside a switch, for the kind of PC chosen in Settings › This PC.
/// Hover says why. Hidden when the switch has no recommendation (a matter of taste).
/// <c>&lt;ctl:RecommendedBadge Item="GamingAutomatic" /&gt;</c>; for lists, bind <see cref="Recommendation"/> instead.
/// </summary>
public sealed class RecommendedBadge : Border
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(string), typeof(RecommendedBadge), new PropertyMetadata(null, (d, _) => ((RecommendedBadge)d).Refresh()));

    public static readonly DependencyProperty RecommendationProperty = DependencyProperty.Register(
        nameof(Recommendation), typeof(Recommendation), typeof(RecommendedBadge), new PropertyMetadata(null, (d, _) => ((RecommendedBadge)d).Refresh()));

    public string? Item
    {
        get => (string?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    /// <summary>Set directly when the recommendation doesn't come from the shared table (privacy switches, cleanup rows).</summary>
    public Recommendation? Recommendation
    {
        get => (Recommendation?)GetValue(RecommendationProperty);
        set => SetValue(RecommendationProperty, value);
    }

    private readonly TextBlock _text = new() { FontSize = 11 };

    public RecommendedBadge()
    {
        Child = _text;
        Padding = new Thickness(6, 1, 6, 1);
        Margin = new Thickness(8, 0, 8, 0);
        CornerRadius = new CornerRadius(4);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "AccentFillColorDefaultBrush");
        _text.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        ToolTipService.SetInitialShowDelay(this, 200);
        Loaded += (_, _) => { RecommendationContext.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => RecommendationContext.Changed -= Refresh;
        Refresh();
    }

    private void Refresh()
    {
        var r = Recommendation ?? RecommendationContext.For(Item);
        if (r is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        _text.Text = r.On ? "Recommended" : "Recommended: off";
        ToolTip = $"Recommended {(r.On ? "on" : "off")} for {Recommendations.Describe(RecommendationContext.Current)}: {r.Why}\n(Change the kind of PC in Settings › This PC.)";
        AutomationProperties.SetName(this, _text.Text);
        AutomationProperties.SetHelpText(this, r.Why);
    }
}
