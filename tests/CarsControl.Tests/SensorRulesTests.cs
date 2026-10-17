using CarsControl.Core.Health;

namespace CarsControl.Tests;

public class SensorRulesTests
{
    private static SensorReading S(string hw, string kind, string name, string type, double? v) => new(hw, kind, name, type, v, v, v);

    // Like the development PC: a Ryzen 9 9900X and a Nuvoton NCT6701D with some unconnected probes and empty fan headers.
    private static readonly SensorReading[] Board =
    [
        S("AMD Ryzen 9 9900X", "Cpu", "Core (Tctl/Tdie)", "Temperature", 48.4),
        S("AMD Ryzen 9 9900X", "Cpu", "CCD1 (Tdie)", "Temperature", 44),
        S("AMD Ryzen 9 9900X", "Cpu", "Package", "Power", 61.3),
        S("AMD Ryzen 9 9900X", "Cpu", "CPU Total", "Load", 7.5),
        S("Nuvoton NCT6701D", "SuperIO", "Temperature #1", "Temperature", 41),
        S("Nuvoton NCT6701D", "SuperIO", "Temperature #3", "Temperature", 3),
        S("Nuvoton NCT6701D", "SuperIO", "Temperature #9", "Temperature", 127),
        S("Nuvoton NCT6701D", "SuperIO", "Fan #1", "Fan", 0),
        S("Nuvoton NCT6701D", "SuperIO", "Fan #2", "Fan", 1277),
        S("Nuvoton NCT6701D", "SuperIO", "Fan #3", "Fan", 999),
    ];

    [Fact]
    public void The_summary_picks_the_cpu_values_and_spinning_fans()
    {
        var s = SensorRules.Summarize(Board);
        Assert.Equal(48.4, s.CpuTempC);
        Assert.Equal(61.3, s.CpuPowerW);
        Assert.Equal(7.5, s.CpuLoadPct);
        Assert.Equal(41, s.BoardTempC);
        Assert.Equal([1277d, 999d], s.Fans.Select(f => f.Rpm));
    }

    [Theory]
    [InlineData("Temperature", 3, false)]
    [InlineData("Temperature", 127, false)]
    [InlineData("Temperature", 41, true)]
    [InlineData("Fan", 0, false)]
    [InlineData("Fan", 65535, false)]
    [InlineData("Fan", 1150, true)]
    [InlineData("Voltage", 0, false)]
    [InlineData("Load", 0, true)]
    public void Impossible_readings_are_recognised(string type, double value, bool plausible) =>
        Assert.Equal(plausible, SensorRules.IsPlausible(S("X", "SuperIO", "x", type, value)));

    [Fact]
    public void Generic_channels_are_not_given_an_invented_cpu_or_fan_connection()
    {
        Assert.Equal("Unassigned temperature probe #1", SensorRules.DisplayName(Board[4]));
        Assert.Equal("Fan header #2 (connection unknown)", SensorRules.DisplayName(Board[8]));
        Assert.Equal("CPU temperature · Core (Tctl/Tdie)", SensorRules.DisplayName(Board[0]));
        Assert.Equal("CPU Fan", SensorRules.DisplayName(S("ITE IT8689E", "SuperIO", "CPU Fan", "Fan", 1100)));
        Assert.Equal("VRM MOS", SensorRules.DisplayName(S("Nuvoton NCT6798D", "SuperIO", "VRM MOS", "Temperature", 45)));
    }

    [Fact]
    public void Intel_names_work_too_and_a_missing_cpu_sensor_falls_back_to_the_board_probe()
    {
        Assert.Equal(66, SensorRules.Summarize([S("Intel Core i7", "Cpu", "CPU Package", "Temperature", 66)]).CpuTempC);
        Assert.Equal(52, SensorRules.Summarize([S("ITE IT8689E", "SuperIO", "CPU", "Temperature", 52)]).CpuTempC);
        Assert.Null(SensorRules.Summarize([]).CpuTempC);
    }
}
