using CarsControl.Core.Health;
using System.Management;

namespace CarsControl.Tests;

public class WmiQueryTests
{
    [Fact]
    public void Wmi_results_are_detached_values_that_survive_disposal_and_later_queries()
    {
        var first = Assert.Single(WmiQuery.Read(@"root\cimv2", "SELECT Caption, BuildNumber FROM Win32_OperatingSystem"));
        var caption = Assert.IsType<string>(first["Caption"]);
        Assert.NotEmpty(caption);
        _ = WmiQuery.Read(@"root\cimv2", "SELECT Caption FROM Win32_OperatingSystem");
        Assert.Equal(caption, first["caption"]);
        Assert.Null(first["NotRequested"]);
        Assert.DoesNotContain(first.Values.Values, value => value is ManagementBaseObject);
    }
}
