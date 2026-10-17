using CarsControl.Core.Palette;

namespace CarsControl.Tests;

public class FuzzyMatcherTests
{
    private static string Best(string query, params string[] options) =>
        options.Select(o => (o, s: FuzzyMatcher.Score(query, o))).Where(x => x.s is not null).OrderByDescending(x => x.s).First().o;

    [Theory]
    [InlineData("upd", "Update everything")]
    [InlineData("update ev", "Update everything")]
    [InlineData("ue", "Update everything")]          // initials
    [InlineData("cfu", "Check for updates")]         // initials
    [InlineData("updte", "Update everything")]       // a missing letter
    [InlineData("udpate", "Update everything")]      // swapped letters
    [InlineData("gamng mode", "Gaming mode settings")]
    [InlineData("steam", "Steam")]
    [InlineData("disc", "Discord")]
    public void Finds_what_you_meant(string query, string text) => Assert.NotNull(FuzzyMatcher.Score(query, text));

    [Theory]
    [InlineData("xyz", "Update everything")]
    [InlineData("zz", "Gaming mode")]
    [InlineData("qwerty", "Steam")]
    public void Unrelated_text_does_not_match(string query, string text) => Assert.Null(FuzzyMatcher.Score(query, text));

    [Fact]
    public void Better_matches_rank_higher()
    {
        Assert.Equal("Steam", Best("steam", "Steam", "Steam Link", "SteamVR", "Wallpaper Engine for Steam"));
        Assert.Equal("Discord", Best("disc", "Microsoft Disk Management", "Discord", "Blu-ray disc player"));
        Assert.Equal("Update everything", Best("upd", "Python packages (pip) updated", "Update everything"));
        // A prefix beats initials, initials beat scattered letters.
        Assert.True(FuzzyMatcher.Score("ga", "Gaming mode") > FuzzyMatcher.Score("gm", "Gaming mode"));
        Assert.True(FuzzyMatcher.Score("gm", "Gaming mode") > FuzzyMatcher.Score("gde", "Gaming mode"));
    }

    [Fact]
    public void Usage_helps_but_does_not_beat_a_much_better_match()
    {
        var exact = FuzzyMatcher.Score("steam", "Steam")!.Value;
        var loose = FuzzyMatcher.Score("steam", "Wallpaper Engine for Steam")!.Value + FuzzyMatcher.UsageBoost(1000);
        Assert.True(exact > loose);
        Assert.Equal(100, FuzzyMatcher.UsageBoost(500)); // capped
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("update", "udpate", 1)]
    [InlineData("abc", "abc", 0)]
    public void Edit_distance(string a, string b, int d) => Assert.Equal(d, FuzzyMatcher.Distance(a, b));

    [Fact]
    public void Empty_query_matches_everything_equally() => Assert.Equal(0, FuzzyMatcher.Score("  ", "Anything"));
}

public class FuzzyNoiseTests
{
    [Theory]
    [InlineData("quiet", "3Nwzq9GRcTjRnGUiyEM5VGy5loGfkj_Ubs5S0rBhqHK13914xdUkvqMA8MVklQzTKmlu5")]
    [InlineData("sched i", "wWOacVpdlpanMDaYqS-lvQnP3ACcfpGHfKJYS2K-7R2bzWUjAV5-jqehS6UdaberyzCVP")]
    [InlineData("gamng", "A Game About Chopping Trees")]
    public void Scattered_letters_in_long_names_do_not_match(string query, string text) => Assert.Null(FuzzyMatcher.Score(query, text));

    [Fact]
    public void Keyword_lists_need_real_words() => Assert.Null(FuzzyMatcher.Score("quiet", "quit close", loose: false));
}
