namespace CarsControl.Core.Util;

/// <summary>Observe detached work immediately; normal cancellation is not an application error.</summary>
public static class BackgroundWork
{
    public static async Task ObserveAsync(Task task, Action<Exception> report)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { report(ex); }
    }
}
