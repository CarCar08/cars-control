using System.Windows;
using System.Windows.Controls;

namespace CarsControl.App.Controls;

/// <summary>
/// Gives plain-text content the wrapping template and leaves everything else to WPF. A ContentTemplate set directly would also
/// be applied to content that's already an element, which then shows as its type name ("System.Windows.Controls.TextBlock").
/// </summary>
public sealed class WrapTextSelector : DataTemplateSelector
{
    public DataTemplate? Text { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item is string ? Text : null;
}
