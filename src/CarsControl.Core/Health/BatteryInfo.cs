using System.Management;

namespace CarsControl.Core.Health;

public sealed record BatteryReading(int ChargePercent, bool Charging, bool OnAc, long? DesignMilliwattHours, long? FullMilliwattHours)
{
    /// <summary>How much of its original capacity the battery has lost (0 = like new).</summary>
    public int? WearPercent => BatteryRules.Wear(DesignMilliwattHours, FullMilliwattHours);

    public string Summary
    {
        get
        {
            var state = Charging ? "charging" : OnAc ? "plugged in" : "on battery";
            var wear = WearPercent is { } w ? $" · holds {100 - w}% of its original capacity" : "";
            return $"{ChargePercent}% ({state}){wear}";
        }
    }
}

public static class BatteryRules
{
    public static int? Wear(long? design, long? full) =>
        design is > 0 && full is > 0 ? (int)Math.Clamp(Math.Round(100.0 * (design.Value - full.Value) / design.Value), 0, 100) : null;

    public static string Verdict(int? wear) => wear switch
    {
        null => "",
        < 15 => "The battery is in good shape.",
        < 35 => "The battery has lost a fair bit of capacity, which is normal with age.",
        _ => "The battery has lost a lot of capacity: runs shorter and may be worth replacing.",
    };
}

/// <summary>The battery of a laptop or tablet. A desktop has none, so this returns null there.</summary>
public static class BatteryReader
{
    public static BatteryReading? Read()
    {
        try
        {
            var b = WmiQuery.Read(@"root\cimv2", "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery").FirstOrDefault();
            if (b is null) return null;
            var status = Convert.ToInt32(b["BatteryStatus"] ?? 0);
            var charge = Convert.ToInt32(b["EstimatedChargeRemaining"] ?? 0);

            long? design = null, full = null;
            try
            {
                design = WmiQuery.Read(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData").Select(o => o["DesignedCapacity"]).Where(v => v is not null).Select(Convert.ToInt64).FirstOrDefault();
                full = WmiQuery.Read(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity").Select(o => o["FullChargedCapacity"]).Where(v => v is not null).Select(Convert.ToInt64).FirstOrDefault();
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                // Without the capacity classes only the charge is shown.
            }
            // 2 = on AC, 6-9 = charging (various levels), 1 = discharging.
            return new BatteryReading(charge, status is 6 or 7 or 8 or 9, status != 1, design is > 0 ? design : null, full is > 0 ? full : null);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
