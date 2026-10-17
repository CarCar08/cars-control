using CarsControl.Core.Health;
using CarsControl.Core.Help;

namespace CarsControl.App.Services;

/// <summary>
/// The kind of PC the Recommended badges are for: the choice in Settings › This PC, with Auto resolved by whether the PC
/// has a battery. Badges listen to <see cref="Changed"/> so switching the choice updates every page at once.
/// </summary>
public static class RecommendationContext
{
    private static bool? _hasBattery;

    public static PcType Chosen { get; private set; } = PcType.Auto;

    public static PcType Current => Recommendations.Resolve(Chosen, HasBattery);

    public static bool HasBattery => _hasBattery ??= SafeHasBattery();

    public static event Action? Changed;

    public static void Set(PcType chosen)
    {
        Chosen = chosen;
        Changed?.Invoke();
    }

    public static Recommendation? For(string? id) => id is null ? null : Recommendations.For(id, Current);

    private static bool SafeHasBattery()
    {
        try
        {
            return BatteryReader.Read() is not null;
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
