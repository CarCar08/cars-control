using System.Globalization;

namespace CarsControl.Core.Optimize;

/// <summary>One recorded stretch of a game: frame rate, 1% low and the per-chunk spread that tells noise from a real change.</summary>
public sealed record FrameSummary(int Frames, double Seconds, double Fps, double OnePercentLowFps, IReadOnlyList<double> ChunkFps, IReadOnlyList<double> ChunkLowFps)
{
    public string Describe() => $"{Fps:0} FPS average, {OnePercentLowFps:0} FPS 1% low ({Frames:N0} frames over {Seconds:0} s)";
}

public enum FrameVerdict { Better, Worse, NoClearDifference, NotEnoughData }

public sealed record FrameComparison(FrameVerdict Verdict, double FpsChangePercent, double LowChangePercent, string Text);

/// <summary>
/// Reads PresentMon's CSV and compares two recordings. A difference only counts when it's clearly larger than the variation
/// inside each recording (each is split into equal chunks) and at least 2%: games vary from moment to moment, and a "gain" smaller
/// than that is noise, whatever the number says.
/// </summary>
public static class FrameStats
{
    public const int Chunks = 6;
    public const double MinimumChangePercent = 2.0;

    /// <summary>Frame times in milliseconds (MsBetweenPresents) for one process, in order. Unreadable rows are skipped.</summary>
    public static IReadOnlyList<double> ParseCsv(IEnumerable<string> lines, int? processId = null)
    {
        var result = new List<double>();
        int frameCol = -1, pidCol = -1;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = line.Split(',');
            if (frameCol < 0)
            {
                frameCol = Array.FindIndex(cells, c => c.Trim().Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
                pidCol = Array.FindIndex(cells, c => c.Trim().Equals("ProcessID", StringComparison.OrdinalIgnoreCase));
                if (frameCol < 0) return result; // not a PresentMon CSV
                continue;
            }
            if (frameCol >= cells.Length) continue;
            if (processId is { } pid && pidCol >= 0 && pidCol < cells.Length && int.TryParse(cells[pidCol], out var rowPid) && rowPid != pid) continue;
            if (double.TryParse(cells[frameCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms is > 0 and < 5000) result.Add(ms);
        }
        return result;
    }

    /// <summary>Summarizes frame times; null when there are too few frames to say anything (under 10 s or 300 frames).</summary>
    public static FrameSummary? Summarize(IReadOnlyList<double> frameMs)
    {
        if (frameMs.Count < 300) return null;
        var totalMs = frameMs.Sum();
        if (totalMs < 10_000) return null;

        // Equal-duration chunks (not equal frame counts), so a slow stretch weighs what it lasted. A frame belongs to the chunk it
        // starts in; a chunk left with almost no frames (a long hitch spanning it) is dropped rather than counted as a wild value.
        var groups = Enumerable.Range(0, Chunks).Select(_ => new List<double>()).ToArray();
        double start = 0, length = totalMs / Chunks;
        foreach (var ms in frameMs)
        {
            groups[Math.Min(Chunks - 1, (int)(start / length))].Add(ms);
            start += ms;
        }
        var kept = groups.Where(g => g.Count >= MinChunkFrames).ToList();
        return new FrameSummary(frameMs.Count, totalMs / 1000, Fps(frameMs), LowFps(frameMs), kept.Select(Fps).ToList(), kept.Select(LowFps).ToList());
    }

    private const int MinChunkFrames = 20;

    public static FrameComparison Compare(FrameSummary? before, FrameSummary? after)
    {
        if (before is null || after is null)
            return new(FrameVerdict.NotEnoughData, 0, 0, "Not enough frames were recorded to compare. Play for the whole minute, in the game (not a menu or loading screen).");

        var fpsChange = Percent(before.Fps, after.Fps);
        var lowChange = Percent(before.OnePercentLowFps, after.OnePercentLowFps);
        var fpsReal = Clear(before.ChunkFps, after.ChunkFps, before.Fps, after.Fps);
        var lowReal = Clear(before.ChunkLowFps, after.ChunkLowFps, before.OnePercentLowFps, after.OnePercentLowFps);

        // Better: something clearly improved and nothing clearly got worse. Worse: the reverse. Anything else is noise.
        var better = (fpsReal && fpsChange > 0) || (lowReal && lowChange > 0);
        var worse = (fpsReal && fpsChange < 0) || (lowReal && lowChange < 0);
        var numbers = $"Average {before.Fps:0} → {after.Fps:0} FPS ({fpsChange:+0.0;-0.0}%), 1% low {before.OnePercentLowFps:0} → {after.OnePercentLowFps:0} FPS ({lowChange:+0.0;-0.0}%).";
        return (better, worse) switch
        {
            (true, false) => new(FrameVerdict.Better, fpsChange, lowChange, "Better on this PC. " + numbers),
            (false, true) => new(FrameVerdict.Worse, fpsChange, lowChange, "Worse on this PC. " + numbers),
            _ => new(FrameVerdict.NoClearDifference, fpsChange, lowChange,
                "No clear difference: the change is within how much the game varies by itself. " + numbers),
        };
    }

    private static double Fps(IReadOnlyCollection<double> ms) => ms.Count == 0 ? 0 : 1000.0 * ms.Count / ms.Sum();

    /// <summary>The frame rate at the 99th percentile frame time: what the slowest 1% of frames feel like.</summary>
    private static double LowFps(IReadOnlyList<double> ms)
    {
        if (ms.Count == 0) return 0;
        var sorted = ms.OrderBy(x => x).ToArray();
        var p99 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.99) - 1)];
        return 1000.0 / p99;
    }

    private static double Percent(double a, double b) => a <= 0 ? 0 : (b - a) / a * 100;

    /// <summary>Whether the difference of the means is more than twice its standard error and at least <see cref="MinimumChangePercent"/>.</summary>
    private static bool Clear(IReadOnlyList<double> a, IReadOnlyList<double> b, double meanA, double meanB)
    {
        if (a.Count < 2 || b.Count < 2) return false;
        var se = Math.Sqrt(Variance(a) / a.Count + Variance(b) / b.Count);
        return Math.Abs(Percent(meanA, meanB)) >= MinimumChangePercent && Math.Abs(meanB - meanA) > 2 * se;
    }

    private static double Variance(IReadOnlyList<double> x)
    {
        var mean = x.Average();
        return x.Sum(v => (v - mean) * (v - mean)) / (x.Count - 1);
    }
}
