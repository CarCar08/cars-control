namespace CarsControl.Core.Util;

public static class ByteSize
{
    /// <summary>"2.1 GB", "640 MB", "0 B".</summary>
    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double b = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b:F0} {units[i]}" : b >= 100 ? $"{b:F0} {units[i]}" : $"{b:F1} {units[i]}";
    }
}
