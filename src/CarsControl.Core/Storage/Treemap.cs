namespace CarsControl.Core.Storage;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height);

/// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk): lays items out in rows that keep
/// rectangles as close to square as possible, so labels stay readable.</summary>
public static class Treemap
{
    public static List<(T Item, TreemapRect Rect)> Layout<T>(IReadOnlyList<(T Item, double Size)> items, TreemapRect bounds)
    {
        var result = new List<(T, TreemapRect)>();
        var positive = items.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
        var total = positive.Sum(i => i.Size);
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        var scale = bounds.Width * bounds.Height / total;
        var queue = new Queue<(T Item, double Area)>(positive.Select(i => (i.Item, i.Size * scale)));
        var rect = bounds;
        var row = new List<(T Item, double Area)>();

        while (queue.Count > 0)
        {
            var next = queue.Peek();
            var side = Math.Min(rect.Width, rect.Height);
            var withNext = new List<(T Item, double Area)>(row) { next };
            if (row.Count == 0 || Worst(row, side) >= Worst(withNext, side))
            {
                row.Add(queue.Dequeue());
                continue;
            }
            rect = PlaceRow(row, rect, result);
            row.Clear();
        }
        if (row.Count > 0) PlaceRow(row, rect, result);
        return result;
    }

    private static double Worst<T>(List<(T Item, double Area)> row, double side) => WorstCore(row.Select(r => r.Area).ToList(), side);

    private static double WorstCore(List<double> areas, double side)
    {
        var sum = areas.Sum();
        if (sum <= 0 || side <= 0) return double.MaxValue;
        var max = areas.Max();
        var min = areas.Min();
        var s2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    private static TreemapRect PlaceRow<T>(List<(T Item, double Area)> row, TreemapRect rect, List<(T, TreemapRect)> output)
    {
        var sum = row.Sum(r => r.Area);
        if (rect.Width >= rect.Height)
        {
            // Column on the left.
            var w = sum / rect.Height;
            var y = rect.Y;
            foreach (var (item, area) in row)
            {
                var h = area / w;
                output.Add((item, new TreemapRect(rect.X, y, w, h)));
                y += h;
            }
            return new TreemapRect(rect.X + w, rect.Y, Math.Max(0, rect.Width - w), rect.Height);
        }
        else
        {
            // Row along the top.
            var h = sum / rect.Width;
            var x = rect.X;
            foreach (var (item, area) in row)
            {
                var w = area / h;
                output.Add((item, new TreemapRect(x, rect.Y, w, h)));
                x += w;
            }
            return new TreemapRect(rect.X, rect.Y + h, rect.Width, Math.Max(0, rect.Height - h));
        }
    }
}
