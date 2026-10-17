namespace CarsControl.Core.Util;

public static class UserData
{
    public static string Root => Resolve(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("CARS_DEV_INSTANCE") == "1", Environment.GetEnvironmentVariable("CARS_DEV_DATA_ROOT"));

    /// <summary>Only an explicit development instance may redirect application-owned data for isolated audits.</summary>
    public static string Resolve(string localAppData, bool devInstance, string? devRoot) =>
        devInstance && !string.IsNullOrWhiteSpace(devRoot) && Path.IsPathFullyQualified(devRoot)
            ? Path.GetFullPath(devRoot) : Path.Combine(localAppData, "CarsControl");
}
