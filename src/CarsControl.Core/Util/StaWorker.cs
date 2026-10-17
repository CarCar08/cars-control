using System.Collections.Concurrent;

namespace CarsControl.Core.Util;

/// <summary>Own synchronous COM work on one background STA. A cancelled caller never tears down the worker's objects.</summary>
internal static class StaWorker
{
    private static readonly BlockingCollection<Action> Queue = new();
    private static int _stopping;

    static StaWorker()
    {
        // Dynamic COM caches type information. Keep its apartment alive across requests instead of
        // allowing a later request to reuse an RCW from an STA that has already exited.
        var thread = new Thread(() => { foreach (var work in Queue.GetConsumingEnumerable()) work(); })
            { IsBackground = true, Name = "WUA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static void Stop() { Interlocked.Exchange(ref _stopping, 1); Queue.CompleteAdding(); }

    public static Task<T> Run<T>(Func<T> work, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => completion.TrySetCanceled(ct));
        void Execute()
        {
            try
            {
                if (Volatile.Read(ref _stopping) != 0) { completion.TrySetCanceled(new CancellationToken(true)); return; }
                ct.ThrowIfCancellationRequested();
                completion.TrySetResult(work());
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == ct) { completion.TrySetCanceled(ct); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { registration.Dispose(); }
        }
        try { Queue.Add(Execute); }
        catch (InvalidOperationException) { registration.Dispose(); completion.TrySetCanceled(new CancellationToken(true)); }
        return completion.Task;
    }
}
