using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CarsControl.App.Services;

/// <summary>Test copies only (CARS_DEV_INSTANCE=1 and CARS_DUMP_VISUALS=1): writes the window's visual
/// tree with sizes and brushes to %TEMP%\CarsControl\visual-tree.txt, to find template parts of
/// third-party controls (e.g. the sidebar's separator lines) without guessing.</summary>
internal static class DevVisualDump
{
    public static bool Enabled => App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DUMP_VISUALS") == "1";

    public static void Write(Visual root)
    {
        var sb = new StringBuilder();
        void Walk(DependencyObject d, int depth)
        {
            if (depth > 60) return;
            var fe = d as FrameworkElement;
            var line = new StringBuilder(new string(' ', depth * 2)).Append(d.GetType().Name);
            if (!string.IsNullOrEmpty(fe?.Name)) line.Append(" #").Append(fe.Name);
            if (fe is not null) line.Append($" {fe.ActualWidth:F0}x{fe.ActualHeight:F0}");
            switch (d)
            {
                case Border b when b.Background is not null || b.BorderThickness != default:
                    line.Append($" bg={Describe(b.Background)} border={Describe(b.BorderBrush)} {b.BorderThickness}");
                    break;
                case Shape s:
                    line.Append($" fill={Describe(s.Fill)} stroke={Describe(s.Stroke)}");
                    break;
                case TextBlock t:
                    line.Append($" \"{t.Text}\" fg={Describe(t.Foreground)}");
                    break;
            }
            sb.AppendLine(line.ToString());
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i), depth + 1);
        }
        Walk(root, 0);

        // Theme resources whose names suggest accent/selection, to see which key a template part uses.
        sb.AppendLine().AppendLine("---- resources ----");
        void Resources(ResourceDictionary d)
        {
            foreach (var key in d.Keys)
                if (key is string k && (k.Contains("Accent") || k.Contains("Navigation") || k.Contains("Indicator") || k.Contains("Pill")))
                    sb.AppendLine($"{k} = {(d[k] is Brush b ? Describe(b) : d[k] is Color c ? c.ToString() : d[k]?.GetType().Name)}");
            foreach (var m in d.MergedDictionaries) Resources(m);
        }
        Resources(Application.Current.Resources);

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CarsControl");
        Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, "visual-tree.txt"), sb.ToString());
    }

    private static string Describe(Brush? b) => b switch
    {
        null => "-",
        SolidColorBrush s => s.Color.ToString(),
        LinearGradientBrush g => "linear(" + string.Join(",", g.GradientStops.Select(x => x.Color.ToString())) + ")",
        _ => b.GetType().Name,
    };
}
