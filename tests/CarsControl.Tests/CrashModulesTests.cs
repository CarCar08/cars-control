using CarsControl.Core.Health;

namespace CarsControl.Tests;

public class CrashModulesTests
{
    [Theory]
    [InlineData("dxgkrnl.sys", "graphics kernel")]
    [InlineData("dxgkrnl", "graphics kernel")]
    [InlineData("nvlddmkm.sys", "NVIDIA")]
    [InlineData("ntoskrnl.exe", "core of Windows")]
    [InlineData("stornvme.sys", "storage")]
    [InlineData("memory_corruption", "corrupted memory")]
    public void Common_culprits_are_explained(string module, string expected) =>
        Assert.Contains(expected, CrashModules.Explain(module));

    [Fact]
    public void Access_violations_are_spelled_out() =>
        Assert.Contains("memory it wasn't allowed", CrashModules.Explain("dxgkrnl.sys", "AV_dxgkrnl!DXGAUTOPUSHLOCKSHARED::DXGAUTOPUSHLOCKSHARED"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("somevendor.sys")]
    public void Unknown_modules_get_no_guess(string? module) => Assert.Null(CrashModules.Explain(module));
}
