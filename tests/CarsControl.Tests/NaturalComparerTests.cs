using CarsControl.Core.Util;

namespace CarsControl.Tests;

public class NaturalComparerTests
{
    [Fact]
    public void Numbers_inside_names_sort_by_value() =>
        Assert.Equal(["Core #1", "Core #2", "Core #9", "Core #10", "Core #12", "core #20", "Voltage"],
            new[] { "Core #10", "Voltage", "Core #2", "core #20", "Core #1", "Core #12", "Core #9" }.OrderBy(s => s, NaturalComparer.Instance));

    [Theory]
    [InlineData("Fan #007", "Fan #7", 0)]
    [InlineData("a", "a1", -1)]
    [InlineData(null, "a", -1)]
    public void Edge_cases(string? a, string? b, int sign) => Assert.Equal(sign, Math.Sign(NaturalComparer.Instance.Compare(a, b)));
}
