using CarsControl.Core.Util;

namespace CarsControl.Tests;

public class UserDataTests
{
    [Fact]
    public void Production_ignores_dev_paths_and_dev_requires_an_absolute_path()
    {
        Assert.Equal(@"C:\UserData\CarsControl", UserData.Resolve(@"C:\UserData", false, @"D:\TestData"));
        Assert.Equal(@"C:\UserData\CarsControl", UserData.Resolve(@"C:\UserData", true, "relative"));
        Assert.Equal(@"D:\TestData", UserData.Resolve(@"C:\UserData", true, @"D:\TestData"));
    }
}
