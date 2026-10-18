using System.Text;
using System.Text.RegularExpressions;

namespace CarsControl.Core.Util;

/// <summary>
/// A bug report the user sends themselves: a link to the repository's bug form with the fields filled in. Nothing is sent by
/// C.A.R.S.; the browser opens the form, the user reads and edits it, and only GitHub's Submit sends it. Everything that goes
/// into the link is first stripped of the user name, the PC name, profile paths and e-mail addresses.
/// </summary>
public static partial class IssueReport
{
    /// <summary>Links longer than this are refused by some browsers and by GitHub; long fields are cut to fit.</summary>
    public const int MaxUrlLength = 7000;

    public static string Sanitize(string text, string userName, string machineName, string profilePath)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = text;
        if (profilePath.Length > 3) result = Regex.Replace(result, Regex.Escape(profilePath.TrimEnd('\\')), @"C:\Users\<you>", RegexOptions.IgnoreCase);
        // Any other account's profile folder (the admin account's, in the helper's paths) is somebody's name too.
        result = OtherProfile().Replace(result, @"$1\<another account>");
        result = Email().Replace(result, "<e-mail>");
        if (userName.Length > 1) result = Regex.Replace(result, @"(?<![\w])" + Regex.Escape(userName) + @"(?![\w])", "<you>", RegexOptions.IgnoreCase);
        if (machineName.Length > 1) result = Regex.Replace(result, @"(?<![\w])" + Regex.Escape(machineName) + @"(?![\w])", "<this PC>", RegexOptions.IgnoreCase);
        return result;
    }

    /// <summary>The error's type and message plus the first frames of its stack (where it happened), for the report.</summary>
    public static string ErrorSummary(Exception ex, int frames = 8)
    {
        var sb = new StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            sb.AppendLine($"{e.GetType().FullName}: {e.Message}");
            if (e.StackTrace is { } trace)
                foreach (var line in trace.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(frames)) sb.AppendLine("  " + line);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The bug form's link with its fields (ids from .github/ISSUE_TEMPLATE/bug_report.yml) filled in.</summary>
    public static string Url(string repositoryUrl, string title, string what, string diagnostics)
    {
        string Build(string w, string d) =>
            $"{repositoryUrl.TrimEnd('/')}/issues/new?template=bug_report.yml&title={Uri.EscapeDataString(title)}" +
            $"&what={Uri.EscapeDataString(w)}&diagnostics={Uri.EscapeDataString(d)}";
        var url = Build(what, diagnostics);
        // Too long: shorten the description (the diagnostics are short and needed), keeping its start.
        var keep = what.Length;
        while (url.Length > MaxUrlLength && keep > 200)
        {
            keep = keep * 3 / 4;
            url = Build(Cut(what, keep) + "\n…(shortened)", diagnostics);
        }
        // Still too long (a huge title or diagnostics): shorten those too rather than open a link GitHub refuses.
        var d = diagnostics.Length;
        while (url.Length > MaxUrlLength && d > 200)
        {
            d = d * 3 / 4;
            url = Build(Cut(what, Math.Min(keep, 200)), Cut(diagnostics, d) + "\n…(shortened)");
        }
        return url;
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")]
    private static partial Regex Email();

    [GeneratedRegex(@"(\b[A-Za-z]:\\Users)\\(?!<you>|<another account>|Public\\|Default\\)[^\\/:*?""<>|\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex OtherProfile();

    /// <summary>The first <paramref name="max"/> characters, never splitting a surrogate pair (half an emoji makes the link invalid).</summary>
    public static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        var n = Math.Max(0, max);
        if (n > 0 && char.IsHighSurrogate(text[n - 1])) n--;
        return text[..n];
    }
}
