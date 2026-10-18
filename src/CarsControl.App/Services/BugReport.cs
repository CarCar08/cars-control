using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CarsControl.Core.Util;

namespace CarsControl.App.Services;

/// <summary>
/// "Report a problem": opens the project's bug form in the browser with the diagnostics (and, from an error message, the error)
/// already filled in, after removing the user name, PC name, profile paths and e-mail addresses. Nothing is sent by C.A.R.S.:
/// the user reads the form and decides whether to submit it. No telemetry.
/// </summary>
public static class BugReport
{
    /// <summary>The project's home on GitHub: the one place to change when the repository moves.</summary>
    public const string ProjectUrl = "https://github.com/CarCar08/cars-control";

    public static string Diagnostics(bool helperConnected)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"C.A.R.S. Control {SelfUpdateService.Current}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Installed copy: {(SelfUpdateService.IsInstalledCopy ? "yes" : "no (development build)")}");
        sb.AppendLine($"Admin helper: {(helperConnected ? "session open" : "no session")}");
        var errors = ErrorReporter.LogPath;
        sb.AppendLine($"Errors logged: {(File.Exists(errors) ? new FileInfo(errors).Length / 1024 + " KB (attach errors.log from the logs folder if relevant)" : "none")}");
        return sb.ToString().TrimEnd();
    }

    public static string Clean(string text) => IssueReport.Sanitize(text, Environment.UserName, Environment.MachineName,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Opens the pre-filled bug form. <paramref name="error"/> comes from the error message's "Report it".</summary>
    public static void Open(bool helperConnected, Exception? error = null)
    {
        var title = error is null ? "" : Clean($"Error: {error.Message}");
        var what = error is null
            ? "What I did:\n\nWhat I expected:\n\nWhat happened instead:\n"
            : "What I was doing when it happened:\n\n\nThe error C.A.R.S. showed (names and paths removed):\n" + Clean(IssueReport.ErrorSummary(error));
        Platform.OpenUri(IssueReport.Url(ProjectUrl, IssueReport.Cut(title, 120), what, Clean(Diagnostics(helperConnected))));
    }
}
