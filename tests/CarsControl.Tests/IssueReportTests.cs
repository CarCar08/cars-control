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
}
