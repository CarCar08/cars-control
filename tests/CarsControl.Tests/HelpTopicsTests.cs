using System.Text.RegularExpressions;
using CarsControl.Core.Help;

namespace CarsControl.Tests;

public class HelpTopicsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CarsControl.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void Every_help_button_in_the_app_points_at_a_real_topic()
    {
        var views = Path.Combine(RepoRoot(), "src", "CarsControl.App", "Views");
        var used = Directory.EnumerateFiles(views, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "HelpButton[^>]*Topic=\"(?<t>[^\"]+)\"").Select(m => (File: Path.GetFileName(f), Topic: m.Groups["t"].Value)))
            .ToList();
        Assert.NotEmpty(used);
        Assert.All(used, u => Assert.True(HelpTopics.Find(u.Topic) is not null, $"{u.File}: unknown help topic '{u.Topic}'"));
    }

    [Fact]
    public void Topics_are_complete_and_unique()
    {
        Assert.Equal(HelpTopics.All.Count, HelpTopics.All.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(HelpTopics.All, t =>
        {
            Assert.Matches("^[a-z]+(-[a-z0-9]+)+$", t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.True(t.What.Length > 20 && t.Does.Length > 20 && t.ToDo.Length > 10 && t.Safety.Length > 5, t.Id);
        });
    }

    [Fact]
    public void Lookup_ignores_case_and_unknown_ids_give_nothing()
    {
        Assert.NotNull(HelpTopics.Find("HEALTH-PAWNIO"));
        Assert.Null(HelpTopics.Find("nope"));
        Assert.Null(HelpTopics.Find(null));
    }
}
