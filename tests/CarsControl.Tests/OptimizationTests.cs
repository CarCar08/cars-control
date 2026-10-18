using CarsControl.Core.Help;
using CarsControl.Core.Optimize;
using CarsControl.Core.Tweaks;
using Microsoft.Win32;

namespace CarsControl.Tests;

public sealed class OptimizationTests : IDisposable
{
    private readonly string _path = $@"Software\CarsControlTests\Optimize-{Guid.NewGuid():N}";
    private readonly RegistryKey _root;

    public OptimizationTests() => _root = Registry.CurrentUser.CreateSubKey(_path);

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
    }

    [Fact]
    public void Every_optimization_says_how_sure_we_are()
    {
        Assert.NotEmpty(Optimizations.All);
        foreach (var t in Optimizations.All)
        {
            Assert.True(t.IsOptimization, $"{t.Id}: optimizations use the optimization groups");
            Assert.False(string.IsNullOrWhiteSpace(t.EvidenceNote), $"{t.Id}: needs a plain sentence on why");
            if (t.Evidence == Evidence.Documented) Assert.StartsWith("https://", t.SourceUrl);
            if (t.SourceUrl is not null) Assert.StartsWith("https://", t.SourceUrl);
        }
    }

    [Fact]
    public void Privacy_and_optimizations_are_separate_and_the_helper_knows_both()
    {
        Assert.All(Optimizations.All, t => Assert.Same(t, TweakCatalog.Find(t.Id)));
        Assert.Contains(TweakCatalog.All, t => !t.IsOptimization);
        Assert.DoesNotContain(TweakCatalog.All, t => !t.IsOptimization && t.Group >= TweakGroup.Power);
    }

    [Fact]
    public void Test_it_switches_are_never_recommended()
    {
        foreach (var type in new[] { PcType.Desktop, PcType.Laptop, PcType.GamingFirst })
        {
            var recs = Recommendations.OptimizationsFor(type);
            foreach (var t in Optimizations.All.Where(t => t.Evidence == Evidence.TestIt))
                Assert.DoesNotContain(recs, r => r.Id == "optimize:" + t.Id);
        }
    }

    [Fact]
    public void Optimization_recommendations_stay_out_of_settings_apply_recommended()
    {
        Assert.DoesNotContain(Recommendations.AllFor(PcType.GamingFirst), r => r.Id.StartsWith("optimize:", StringComparison.Ordinal));
        Assert.Contains(Recommendations.OptimizationsFor(PcType.GamingFirst), r => r.Id == "optimize:game-mode" && r.On);
    }

    [Fact]
    public void Laptops_keep_hibernation()
    {
        Assert.False(Recommendations.For("optimize:hibernation-off", PcType.Laptop)!.On);
        Assert.True(Recommendations.For("optimize:hibernation-off", PcType.Desktop)!.On);
    }

    [Fact]
    public void Game_mode_reads_on_when_windows_uses_its_default()
    {
        var gameMode = Optimizations.All.Single(t => t.Id == "game-mode");
        Assert.True(gameMode.RestoresWindowsDefault);
        Assert.Equal(TweakState.On, TweakEngine.Read(gameMode, _root)); // value absent: Windows' default, on

        using (var k = _root.CreateSubKey(@"Software\Microsoft\GameBar")) k.SetValue("AutoGameModeEnabled", 0, RegistryValueKind.DWord);
        Assert.Equal(TweakState.Off, TweakEngine.Read(gameMode, _root)); // turned off by something else

        var saved = TweakEngine.Snapshot(gameMode, _root);
        TweakEngine.Apply(gameMode, _root);
        Assert.Equal(TweakState.On, TweakEngine.Read(gameMode, _root));
        TweakEngine.Restore(gameMode, saved, _root);
        Assert.Equal(TweakState.Off, TweakEngine.Read(gameMode, _root)); // undo puts back exactly what was there
    }

    [Theory]
    [InlineData(PowerMode.BestEfficiency)]
    [InlineData(PowerMode.Balanced)]
    [InlineData(PowerMode.BestPerformance)]
    public void Power_modes_round_trip_through_their_overlay(PowerMode mode) =>
        Assert.Equal(mode, SystemSwitches.ModeFor(SystemSwitches.OverlayFor(mode)));
}
