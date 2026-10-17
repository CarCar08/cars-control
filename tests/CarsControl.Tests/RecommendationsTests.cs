using CarsControl.Core.Help;

namespace CarsControl.Tests;

public class RecommendationsTests
{
    [Theory]
    [InlineData(PcType.Auto, true, PcType.Laptop)]
    [InlineData(PcType.Auto, false, PcType.Desktop)]
    [InlineData(PcType.GamingFirst, true, PcType.GamingFirst)]
    [InlineData(PcType.Desktop, true, PcType.Desktop)]
    public void Auto_means_laptop_only_when_there_is_a_battery(PcType chosen, bool battery, PcType expected) =>
        Assert.Equal(expected, Recommendations.Resolve(chosen, battery));

    [Fact]
    public void Laptops_get_their_own_advice()
    {
        Assert.True(Recommendations.For("RestartAway", PcType.Desktop)!.On);
        var laptop = Recommendations.For("RestartAway", PcType.Laptop)!;
        Assert.False(laptop.On);
        Assert.Contains("sleep", laptop.Why);
        Assert.Null(Recommendations.For("GamingApproveAtSignIn", PcType.Laptop));
        Assert.True(Recommendations.For("GamingFreeStandby", PcType.GamingFirst)!.On);
        Assert.Null(Recommendations.For("GamingFreeStandby", PcType.Desktop));
    }

    [Fact]
    public void Pre_releases_are_never_recommended_and_unknown_switches_have_no_advice()
    {
        Assert.All(Enum.GetValues<PcType>(), t => Assert.False(Recommendations.For("UpdatesPre", t)!.On));
        Assert.Null(Recommendations.For("NoSuchSwitch", PcType.Desktop));
    }

    [Fact]
    public void Privacy_and_cleanup_follow_their_own_rules()
    {
        Assert.True(Recommendations.ForPrivacy("advertising-id", "No advertising ID", isTaskbar: false, hasCaution: false)!.On);
        Assert.Null(Recommendations.ForPrivacy("task-view-button", "No Task View button", isTaskbar: true, hasCaution: false));
        Assert.Null(Recommendations.ForPrivacy("x", "X", isTaskbar: false, hasCaution: true));
        Assert.False(Recommendations.ForCleanup("recycle-bin", "Empty the Recycle Bin", defaultOn: false).On);
    }

    [Fact]
    public void Every_recommendation_explains_itself()
    {
        foreach (var t in Enum.GetValues<PcType>())
            Assert.All(Recommendations.AllFor(t), r => Assert.True(r.Why.Length > 20 && r.Title.Length > 3, r.Id));
    }
}
