namespace CarsControl.Core.Util;

public static class VersionCompare
{
    /// <summary>True when <paramref name="candidate"/> is a higher version than <paramref name="current"/>.
    /// Missing components count as 0, so 1.0.6.0 equals 1.0.6. Non-numeric parts compare as text.</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(current)) return true;
        return Compare(candidate, current) > 0;
    }

    public static int Compare(string a, string b)
    {
        var pa = Split(a);
        var pb = Split(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : "0";
            var y = i < pb.Length ? pb[i] : "0";
            var c = long.TryParse(x, out var nx) && long.TryParse(y, out var ny)
                ? nx.CompareTo(ny)
                : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return 0;
    }

    private static string[] Split(string v) =>
        v.Trim().TrimStart('v', 'V').Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
}
