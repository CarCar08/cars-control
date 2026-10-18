using CarsControl.App.ViewModels;

namespace CarsControl.App.Services;

/// <summary>
/// The fixed drives and their space, read on a worker thread. Reading a drive that has spun down (a backup hard disk) can take
/// seconds, so nothing on the UI thread asks Windows directly: Home, Storage and the command palette read <see cref="Last"/> and
/// call <see cref="ReadAsync"/> to refresh it.
/// </summary>
public static class DriveSnapshot
{
    public static IReadOnlyList<DriveCard> Last { get; private set; } = [];

    /// <summary>Raised after every read, on the thread that called <see cref="ReadAsync"/> (the UI thread for the pages).</summary>
    public static event Action? Changed;

    public static async Task<IReadOnlyList<DriveCard>> ReadAsync()
    {
        var read = await Task.Run(() => DriveCard.ReadAll().ToList());
        Last = read;
        Changed?.Invoke();
        return read;
    }
}
