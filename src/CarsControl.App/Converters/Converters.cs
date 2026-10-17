using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CarsControl.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

/// <summary>0 → visible (an "empty" message). Invert: 0 → collapsed (something to show only when there is some).</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value is 0) != Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BytesTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Format.Bytes(value as long?);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Carries the page's DataContext into places outside the visual tree (context menus, item
/// templates) without x:Reference, which fails on ancestors with a cyclic-dependency error.</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>false: null → Collapsed. true: null → Visible (shows a fallback).</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var isNull = value is null || value is string { Length: 0 };
        return isNull ^ Invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Empty text → null, so a ToolTip bound to it doesn't pop up as an empty box.</summary>
public sealed class EmptyToNullConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value is string { Length: 0 } ? null : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"#RRGGBB" → a brush (the custom accent swatch); anything else → transparent.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        CarsControl.App.Services.AppearanceService.TryParse(value as string, out var c) ? new System.Windows.Media.SolidColorBrush(c) : System.Windows.Media.Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A string that's null or empty collapses the element (the XAML-only twin of NullToCollapsed for text).</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>An enum tab ↔ the TabControl's SelectedIndex.</summary>
public sealed class TabIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is Enum e ? System.Convert.ToInt32(e, culture) : 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i and >= 0 ? Enum.ToObject(targetType, i) : Binding.DoNothing;
}

/// <summary>
/// A banner's InfoBar.IsOpen, two-way against its message: open while there's text, and closing it with ✕ clears the
/// message (so it really goes away instead of being drawn again by its Visibility binding).
/// </summary>
public sealed class OpenWhileSetConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value is string { Length: > 0 } || (value is not null and not string);

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is false ? null : Binding.DoNothing;
}

/// <summary>A banner's meaning → its color. Info stays neutral instead of using the (pink-red) accent.</summary>
public sealed class NoticeSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        CarsControl.App.ViewModels.NoticeKind.Success => Wpf.Ui.Controls.InfoBarSeverity.Success,
        CarsControl.App.ViewModels.NoticeKind.Warning => Wpf.Ui.Controls.InfoBarSeverity.Warning,
        CarsControl.App.ViewModels.NoticeKind.Error => Wpf.Ui.Controls.InfoBarSeverity.Error,
        _ => Wpf.Ui.Controls.InfoBarSeverity.Informational,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Sort direction → arrow icon for the sort-direction buttons.</summary>
public sealed class SortSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Wpf.Ui.Controls.SymbolRegular.ArrowSortDown24 : Wpf.Ui.Controls.SymbolRegular.ArrowSortUp24;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class Format
{
    public static string Bytes(long? bytes) => bytes is null or <= 0 ? "—" : Core.Util.ByteSize.Format(bytes.Value);

    public static string Ago(DateTime when)
    {
        var d = DateTime.Now - when;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} d ago";
    }

    /// <summary>How long ago, compactly: "3 d", "8 mo", "2.4 y".</summary>
    public static string Age(DateTime when)
    {
        var days = (DateTime.Now - when).TotalDays;
        return days < 60 ? $"{Math.Max(0, (int)days)} d" : days < 365 ? $"{(int)(days / 30.4)} mo" : $"{days / 365:F1} y";
    }
}
