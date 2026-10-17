using System.Text.RegularExpressions;
using CarsControl.Core.Util;

namespace CarsControl.Core.Winget;

/// <summary>What `winget show` says about a package that's worth a link: the release notes page, the
/// homepage, the publisher's site.</summary>
public sealed record WingetPackageInfo(string? ReleaseNotesUrl, string? Homepage, string? PublisherUrl, string? Description, string? License)
{
    /// <summary>The best page to read what changed: release notes if the package has them, else the homepage.</summary>
    public string? BestUrl => ReleaseNotesUrl ?? Homepage ?? PublisherUrl;
}

public static partial class WingetShow
{
    /// <summary>Runs `winget show` as you (read-only, no admin) and parses it.</summary>
    public static async Task<WingetPackageInfo?> ShowAsync(string id, CancellationToken ct = default)
    {
        if (!WingetCli.IsValidId(id) || WingetCli.Locate() is not { } winget) return null;
        var r = await ProcessRunner.RunAsync(winget, ["show", "--id", id, "--exact", "--accept-source-agreements", "--disable-interactivity"], null, TimeSpan.FromSeconds(45), ct);
        return r.ExitCode == 0 && !r.TimedOut ? Parse(r.Lines) : null;
    }

    public static WingetPackageInfo Parse(IEnumerable<string> lines)
    {
        string? Field(string label) =>
            lines.Select(l => LabelValue().Match(l)).Where(m => m.Success && m.Groups[1].Value.Equals(label, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Groups[2].Value.Trim()).FirstOrDefault(v => v.Length > 0);

        static string? Web(string? url) => url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? u.AbsoluteUri : null;

        return new WingetPackageInfo(
            Web(Field("Release Notes Url")),
            Web(Field("Homepage")),
            Web(Field("Publisher Url")),
            Field("Description"),
            Field("License"));
    }

    [GeneratedRegex(@"^\s*([A-Za-z][A-Za-z ]+?):\s*(.*)$")]
    private static partial Regex LabelValue();
}
