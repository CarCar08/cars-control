using CarsControl.Core.Util;

namespace CarsControl.Tests;

public class IssueReportTests
{
    [Fact]
    public void Personal_details_are_removed_before_anything_goes_into_the_link()
    {
        var text = @"Could not read C:\Users\Alex\AppData\Local\thing.db on DESK-01 for alex (alex.smith@example.com); Alexandra is fine";
        var clean = IssueReport.Sanitize(text, "Alex", "DESK-01", @"C:\Users\Alex");
        Assert.Equal(@"Could not read C:\Users\<you>\AppData\Local\thing.db on <this PC> for <you> (<e-mail>); Alexandra is fine", clean);
    }

    [Fact]
    public void The_link_fills_the_bug_form_and_stays_short_enough()
    {
        var url = IssueReport.Url("https://github.com/owner/repo/", "Crash: X", new string('a', 20_000), "C.A.R.S. Control 1.0.0");
        Assert.StartsWith("https://github.com/owner/repo/issues/new?template=bug_report.yml&title=Crash%3A%20X&what=", url);
        Assert.Contains("&diagnostics=C.A.R.S.%20Control%201.0.0", url);
        Assert.True(url.Length <= IssueReport.MaxUrlLength, $"{url.Length}");
        Assert.Contains(Uri.EscapeDataString("…(shortened)"), url);
    }

    [Fact]
    public void Error_summaries_include_inner_errors_and_where_they_happened()
    {
        Exception caught;
        try { throw new InvalidOperationException("outer", new IOException("disk said no")); }
        catch (Exception ex) { caught = ex; }
        var summary = IssueReport.ErrorSummary(caught);
        Assert.Contains("System.InvalidOperationException: outer", summary);
        Assert.Contains("System.IO.IOException: disk said no", summary);
        Assert.Contains(nameof(Error_summaries_include_inner_errors_and_where_they_happened), summary);
    }

    [Fact]
    public void Other_accounts_profile_folders_are_removed_too()
    {
        var clean = IssueReport.Sanitize(@"Setup: C:\Users\JohnAdmin\AppData\Local\Temp\WinGet\x.exe and C:\Users\Alex\Desktop and C:\Users\Public\Documents",
            "Alex", "DESK-01", @"C:\Users\Alex");
        Assert.DoesNotContain("JohnAdmin", clean);
        Assert.Contains(@"C:\Users\<another account>\AppData", clean);
        Assert.Contains(@"C:\Users\<you>\Desktop", clean);
        Assert.Contains(@"C:\Users\Public\Documents", clean); // not anybody's name
    }

    [Fact]
    public void Cutting_never_splits_an_emoji()
    {
        var text = "ab\U0001F600cd"; // a, b, two UTF-16 units for the emoji, c, d
        Assert.Equal("ab", IssueReport.Cut(text, 3));
        Assert.Equal("ab\U0001F600", IssueReport.Cut(text, 4));
        _ = Uri.EscapeDataString(IssueReport.Cut(text, 3)); // would throw on half an emoji
    }

    [Fact]
    public void Even_huge_diagnostics_fit_the_link_limit()
    {
        var url = IssueReport.Url("https://github.com/x/y", "t", "what", new string('d', 20_000));
        Assert.True(url.Length <= IssueReport.MaxUrlLength);
        Assert.Contains("template=bug_report.yml", url);
    }
}
