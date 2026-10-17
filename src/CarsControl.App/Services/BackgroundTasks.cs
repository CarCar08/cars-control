namespace CarsControl.App.Services;

internal static class BackgroundTasks
{
    public static void Run(Task task, string name) =>
        _ = Core.Util.BackgroundWork.ObserveAsync(task, ex => ErrorReporter.Log(name, ex));
}
