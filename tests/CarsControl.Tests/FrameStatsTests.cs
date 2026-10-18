using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Optimize;

namespace CarsControl.Tests;

public sealed class FrameStatsTests
{
    /// <summary>A minute of frames around <paramref name="ms"/> with a little jitter and an occasional hitch, reproducibly.</summary>
    private static List<double> Run(double ms, int seed, double jitter = 0.08, double seconds = 60)
    {
        var rng = new Random(seed);
        var list = new List<double>();
        double total = 0;
        while (total < seconds * 1000)
        {
            var f = ms * (1 + (rng.NextDouble() * 2 - 1) * jitter);
            if (rng.NextDouble() < 0.005) f *= 3; // a hitch now and then
            list.Add(f);
            total += f;
        }
        return list;
    }

    [Fact]
    public void Reads_presentmon_2_csv_for_one_process()
    {
        string[] csv =
        [
            "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,MsBetweenPresents,MsInPresentAPI",
            "game.exe,4242,0x1,DXGI,0,0,1,Hardware: Independent Flip,0.010,16.6,0.2",
            "game.exe,4242,0x1,DXGI,0,0,1,Hardware: Independent Flip,0.027,NA,0.2",
            "other.exe,99,0x2,DXGI,0,0,1,Composed: Flip,0.030,8.3,0.1",
            "game.exe,4242,0x1,DXGI,0,0,1,Hardware: Independent Flip,0.043,16.7,0.2",
        ];
        Assert.Equal([16.6, 16.7], FrameStats.ParseCsv(csv, 4242));
        Assert.Empty(FrameStats.ParseCsv(["not,a,presentmon,file", "1,2,3,4"]));
    }

    [Fact]
    public void Summary_needs_enough_frames()
    {
        Assert.Null(FrameStats.Summarize(Run(16.7, 1, seconds: 5)));
        var s = FrameStats.Summarize(Run(16.7, 1))!;
        Assert.InRange(s.Fps, 57, 62);
        Assert.True(s.OnePercentLowFps < s.Fps);
        Assert.Equal(FrameStats.Chunks, s.ChunkFps.Count);
    }

    [Fact]
    public void Same_game_twice_is_no_clear_difference()
    {
        var c = FrameStats.Compare(FrameStats.Summarize(Run(10, 1)), FrameStats.Summarize(Run(10, 2)));
        Assert.Equal(FrameVerdict.NoClearDifference, c.Verdict);
    }

    [Fact]
    public void A_real_ten_percent_gain_is_better_and_a_loss_is_worse()
    {
        var slow = FrameStats.Summarize(Run(11, 3));
        var fast = FrameStats.Summarize(Run(10, 4));
        Assert.Equal(FrameVerdict.Better, FrameStats.Compare(slow, fast).Verdict);
        Assert.Equal(FrameVerdict.Worse, FrameStats.Compare(fast, slow).Verdict);
    }

    [Fact]
    public void A_one_percent_change_is_never_called_a_gain()
    {
        // Steady frames make the noise tiny, but 1% is still below the minimum worth claiming.
        var c = FrameStats.Compare(FrameStats.Summarize(Run(10.1, 5, jitter: 0.001)), FrameStats.Summarize(Run(10, 6, jitter: 0.001)));
        Assert.Equal(FrameVerdict.NoClearDifference, c.Verdict);
    }

    [Fact]
    public void Missing_data_says_so()
    {
        Assert.Equal(FrameVerdict.NotEnoughData, FrameStats.Compare(null, FrameStats.Summarize(Run(10, 7))).Verdict);
    }
}

public sealed class FrameCaptureOpTests
{
    private static Task<OpResult> Run(HelperOp op) =>
        new HelperOperations(new HelperLog(), parentProfile: null).ExecuteAsync(op, dryRun: true, _ => { }, CancellationToken.None);

    [Theory]
    [InlineData(5)]
    [InlineData(121)]
    public async Task Only_ten_to_one_hundred_twenty_seconds(int seconds) =>
        Assert.Equal(OpStatus.Rejected, (await Run(new FrameCaptureOp(Environment.ProcessId, seconds))).Status);

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Never_system_processes(int pid) =>
        Assert.Equal(OpStatus.Rejected, (await Run(new FrameCaptureOp(pid, 60))).Status);

    [Fact]
    public async Task A_process_that_is_gone_is_refused()
    {
        var gone = Enumerable.Range(60_000, 2_000).First(p => { try { System.Diagnostics.Process.GetProcessById(p).Dispose(); return false; } catch (ArgumentException) { return true; } });
        Assert.Equal(OpStatus.Rejected, (await Run(new FrameCaptureOp(gone, 60))).Status);
    }

    [Fact]
    public async Task Dry_run_names_the_process_and_records_nothing()
    {
        var r = await Run(new FrameCaptureOp(Environment.ProcessId, 60));
        Assert.Equal(OpStatus.DryRun, r.Status);
        Assert.Contains(System.Diagnostics.Process.GetCurrentProcess().ProcessName, r.Message);
    }
}

public sealed class FrameChunkTests
{
    [Fact]
    public void A_long_hitch_does_not_make_a_one_frame_chunk()
    {
        var frames = Enumerable.Repeat(10.0, 3000).ToList(); // 30 s at 100 FPS
        frames.Insert(1500, 12_000);                         // a 12 s freeze spanning more than one chunk
        var s = FrameStats.Summarize(frames)!;
        Assert.All(s.ChunkFps, fps => Assert.InRange(fps, 1, 200));
        Assert.True(s.ChunkFps.Count is >= 2 and <= FrameStats.Chunks);
    }
}
