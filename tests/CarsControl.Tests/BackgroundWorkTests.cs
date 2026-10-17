using CarsControl.Core.Util;

namespace CarsControl.Tests;

public class BackgroundWorkTests
{
    [Fact]
    public async Task Detached_failures_are_observed_and_cancellation_is_quiet()
    {
        var errors = new List<Exception>();
        var failure = new IOException("failure");
        await BackgroundWork.ObserveAsync(Task.FromException(failure), errors.Add);
        await BackgroundWork.ObserveAsync(Task.FromCanceled(new CancellationToken(true)), errors.Add);
        await BackgroundWork.ObserveAsync(Task.CompletedTask, errors.Add);
        Assert.Same(failure, Assert.Single(errors));
    }

    [Fact]
    public async Task Sta_work_runs_on_one_sta_and_returns_only_the_managed_result()
    {
        var state = await StaWorker.Run(() => (Thread.CurrentThread.GetApartmentState(), Thread.CurrentThread.IsBackground), CancellationToken.None);
        Assert.Equal(ApartmentState.STA, state.Item1);
        Assert.True(state.Item2);
    }

    [Fact]
    public async Task A_pre_cancelled_sta_request_never_runs_the_work()
    {
        var ran = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StaWorker.Run(() => { ran = true; return 1; }, new CancellationToken(true)));
        Assert.False(ran);
    }

    [Fact]
    public async Task Concurrent_com_requests_share_the_same_living_apartment()
    {
        var threads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => StaWorker.Run(() => Environment.CurrentManagedThreadId, CancellationToken.None)));
        Assert.Single(threads.Distinct());
    }

    [Fact]
    public async Task Cancellation_does_not_dispose_resources_still_owned_by_the_sta()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var work = StaWorker.Run(() =>
        {
            entered.Set();
            try { Assert.True(release.Wait(TimeSpan.FromSeconds(5))); return 1; }
            finally { finished.Set(); }
        }, cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            Assert.False(finished.IsSet);
        }
        finally { release.Set(); Assert.True(finished.Wait(TimeSpan.FromSeconds(5))); }
    }
}
