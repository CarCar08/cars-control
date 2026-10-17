using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CarsControl.App.Converters;
using CarsControl.Core.Storage;

namespace CarsControl.App.Controls;

/// <summary>Squarified treemap of a folder's subfolders (plus a "files here" block). Click a block to
/// drill in; hover for the full name and size.</summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty NodeProperty = DependencyProperty.Register(
        nameof(Node), typeof(DirNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DirNode? Node
    {
        get => (DirNode?)GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    /// <summary>Raised when a subfolder block is clicked.</summary>
    public event Action<DirNode>? NodeActivated;

    private List<(object Item, Rect Rect)> _blocks = [];
    private const int MaxBlocks = 80;
    private static readonly Typeface Face = new("Segoe UI");

    public TreemapControl()
    {
        ToolTipService.SetInitialShowDelay(this, 150);
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnClick;
        Cursor = Cursors.Hand;
    }

    private sealed record FilesBlock(DirNode Owner);
    private sealed record OtherBlock(long Bytes, int Count);

    protected override void OnRender(DrawingContext dc)
    {
        _blocks = [];
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var node = Node;
        if (node is null || node.TotalBytes <= 0 || ActualWidth < 10 || ActualHeight < 10) return;

        var items = new List<(object Item, double Size)>();
        foreach (var c in node.Children.Take(MaxBlocks)) items.Add((c, c.TotalBytes));
        var rest = node.Children.Skip(MaxBlocks).ToList();
        if (rest.Count > 0) items.Add((new OtherBlock(rest.Sum(r => r.TotalBytes), rest.Count), rest.Sum(r => r.TotalBytes)));
        if (node.OwnFilesBytes > 0) items.Add((new FilesBlock(node), node.OwnFilesBytes));

        var laid = Treemap.Layout<object>(items, new TreemapRect(0, 0, ActualWidth, ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var i = 0;
        foreach (var (item, r) in laid)
        {
            var rect = new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2));
            _blocks.Add((item, rect));
            var color = item switch
            {
                FilesBlock => Color.FromRgb(0x5A, 0x5A, 0x5A),
                OtherBlock => Color.FromRgb(0x46, 0x46, 0x46),
                _ => Hue(i++),
            };
            dc.DrawRoundedRectangle(new SolidColorBrush(color), null, rect, 4, 4);

            if (rect.Width < 70 || rect.Height < 34) continue;
            var (label, size) = Describe(item);
            var title = Text(label, 13, FontWeights.SemiBold, rect.Width - 12, dpi);
            var sub = Text(Format.Bytes(size), 12, FontWeights.Normal, rect.Width - 12, dpi);
            dc.DrawText(title, new Point(rect.X + 6, rect.Y + 5));
            if (rect.Height > 44) dc.DrawText(sub, new Point(rect.X + 6, rect.Y + 5 + title.Height));
        }
    }

    private static FormattedText Text(string s, double size, FontWeight weight, double maxWidth, double dpi)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Face.FontFamily, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White, dpi)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return ft;
    }

    private static (string Label, long Size) Describe(object item) => item switch
    {
        DirNode d => (d.Name + (d.Inaccessible ? " (partly unreadable)" : ""), d.TotalBytes),
        FilesBlock f => ("Files in this folder", f.Owner.OwnFilesBytes),
        OtherBlock o => ($"{o.Count} smaller folders", o.Bytes),
        _ => ("", 0),
    };

    /// <summary>Muted, evenly spaced hues that read well under white text in both themes. The biggest
    /// folder gets C.A.R.S. teal (starting at red made the largest tile look like an alarm).</summary>
    private static Color Hue(int index)
    {
        var h = (170 + index * 137.508) % 360; // golden angle, from teal
        return FromHsl(h, 0.38, 0.36);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private object? HitTest(Point p) => _blocks.FirstOrDefault(b => b.Rect.Contains(p)).Item;

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var item = HitTest(e.GetPosition(this));
        var (label, size) = item is null ? ("", 0L) : Describe(item);
        var tip = item is null ? "Each rectangle represents a folder or file group; area shows its share of this scan. Click a folder to explore it. No files are changed."
            : $"{label}\n{Format.Bytes(size)}" + (Node is { TotalBytes: > 0 } n ? $" · {size * 100.0 / n.TotalBytes:F1}% of this scan" : "") + "\nClick a folder to explore it; no files are changed.";
        if (!Equals(ToolTip, tip)) ToolTip = tip;
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (HitTest(e.GetPosition(this)) is DirNode d && d.Children.Count + d.OwnFileCount > 0) NodeActivated?.Invoke(d);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
