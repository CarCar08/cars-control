using System.Net;
using System.Text.Json;
using CarsControl.Core.Alerts;
using CarsControl.Core.Settings;

namespace CarsControl.Tests;

public class AlertMessageTests
{
    private static IEnumerable<Alert> Every(string? label)
    {
        var at = new DateTime(2026, 10, 13, 14, 32, 0);
        yield return AlertMessages.Test(label);
        yield return AlertMessages.MaintenanceSummary(label, 5, 0, 2_254_857_830, 3);
        yield return AlertMessages.MaintenanceSummary(label, 1, 2, 0, 0);
        yield return AlertMessages.ApprovalsWaiting(label, 3);
        yield return AlertMessages.LowDisk(label, 'C', 18L * 1024 * 1024 * 1024, 4.2);
        yield return AlertMessages.DriveFillingFast(label, 'A', 15L * 1024 * 1024 * 1024);
        yield return AlertMessages.BlueScreen(label, at);
        yield return AlertMessages.UnexpectedShutdown(label, at);
        yield return AlertMessages.DriveHealth(label, 'D', @"Samsung SSD 990 PRO S/N 12345 at C:\Users\alex predicted failure");
        yield return AlertMessages.WeeklyDigest(label, 12, 9);
    }

    [Fact]
    public void Alerts_carry_no_paths_names_or_error_text()
    {
        var user = Environment.UserName;
        foreach (var a in Every(null).Concat(Every("Desktop")))
        {
            var text = a.Title + " " + a.Message;
            Assert.DoesNotContain('\\', text);
            Assert.DoesNotContain(@":\", text);
            Assert.DoesNotContain("Users", text);
            Assert.DoesNotContain("Samsung", text);  // device text from health status never passes through
            Assert.DoesNotContain("12345", text);
            if (user.Length > 2) Assert.DoesNotContain(user, text, StringComparison.OrdinalIgnoreCase);
            Assert.True(text.Length < 200, $"too long: {text}");
        }
    }

    [Fact]
    public void Summaries_read_naturally()
    {
        var ok = AlertMessages.MaintenanceSummary(null, 5, 0, 2_254_857_830, 3);
        Assert.Equal("C.A.R.S.: maintenance finished", ok.Title);
        Assert.Equal("5 updated, 2.1 GB cleaned, 3 waiting for your approval.", ok.Message);
        var bad = AlertMessages.MaintenanceSummary("Desktop", 1, 2, 0, 0);
        Assert.Equal("C.A.R.S. · Desktop: maintenance had problems", bad.Title);
        Assert.StartsWith("2 items failed.", bad.Message);
    }

    [Fact]
    public void Only_drive_health_and_a_nearly_full_drive_are_critical()
    {
        Assert.True(AlertMessages.DriveHealth(null, 'C', "Unhealthy").Critical);
        Assert.True(AlertMessages.LowDisk(null, 'C', 1L * 1024 * 1024 * 1024, 0.5).Critical);
        Assert.False(AlertMessages.LowDisk(null, 'C', 15L * 1024 * 1024 * 1024, 5).Critical);
        Assert.False(AlertMessages.BlueScreen(null, DateTime.Now).Critical);
    }
}

public class AlertPolicyTests
{
    private static AlertSettings Quiet(string from = "23:00", string to = "08:00") => new() { Enabled = true, QuietHours = true, QuietFrom = from, QuietTo = to };

    [Theory]
    [InlineData(23, 30, true)]
    [InlineData(2, 0, true)]
    [InlineData(7, 59, true)]
    [InlineData(8, 0, false)]
    [InlineData(14, 0, false)]
    [InlineData(22, 59, false)]
    public void Quiet_hours_wrap_midnight(int h, int m, bool quiet) =>
        Assert.Equal(quiet, AlertPolicy.InQuietHours(Quiet(), new DateTime(2026, 10, 13, h, m, 0)));

    [Fact]
    public void Daytime_quiet_hours_and_switched_off()
    {
        Assert.True(AlertPolicy.InQuietHours(Quiet("13:00", "14:00"), new DateTime(2026, 10, 13, 13, 30, 0)));
        Assert.False(AlertPolicy.InQuietHours(Quiet("13:00", "14:00"), new DateTime(2026, 10, 13, 23, 30, 0)));
        var off = Quiet();
        off.QuietHours = false;
        Assert.False(AlertPolicy.InQuietHours(off, new DateTime(2026, 10, 13, 2, 0, 0)));
    }

    [Fact]
    public void Critical_alerts_go_through_quiet_hours()
    {
        var night = new DateTime(2026, 10, 13, 3, 0, 0);
        Assert.False(AlertPolicy.SendNow(AlertMessages.BlueScreen(null, night), Quiet(), night));
        Assert.True(AlertPolicy.SendNow(AlertMessages.DriveHealth(null, 'C', "Warning"), Quiet(), night));
    }

    [Fact]
    public void Each_event_follows_its_switch()
    {
        var s = new AlertSettings { Crashes = false, LowDisk = true };
        Assert.False(AlertPolicy.Wanted(AlertMessages.BlueScreen(null, DateTime.Now), s));
        Assert.True(AlertPolicy.Wanted(AlertMessages.LowDisk(null, 'C', 1, 1), s));
        Assert.True(AlertPolicy.Wanted(AlertMessages.Test(null), new AlertSettings { Failures = false, Crashes = false }));
    }

    [Fact]
    public void Topics_are_random_and_valid()
    {
        var a = AlertPolicy.NewTopic();
        var b = AlertPolicy.NewTopic();
        Assert.NotEqual(a, b);
        Assert.True(AlertPolicy.IsValidTopic(a));
        Assert.StartsWith("cars-", a);
        Assert.Equal(27, a.Length);
        Assert.False(AlertPolicy.IsValidTopic("short"));
        Assert.False(AlertPolicy.IsValidTopic("has space in it"));
        Assert.False(AlertPolicy.IsValidTopic("../../etc"));
        Assert.True(AlertPolicy.IsValidServer("https://ntfy.sh"));
        Assert.False(AlertPolicy.IsValidServer("http://ntfy.sh"));
        Assert.False(AlertPolicy.IsValidServer("ntfy.sh"));
    }
}

public class NtfyClientTests
{
    private sealed class Capture(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status);
        }
    }

    [Fact]
    public async Task Publishes_json_to_the_server_root_with_an_optional_token()
    {
        var handler = new Capture(HttpStatusCode.OK);
        var client = new NtfyClient(new HttpClient(handler));
        var s = new AlertSettings { Server = "https://ntfy.example.com/", Topic = "cars-abcdefghijkmnpqrstuvw" };
        var (ok, _) = await client.SendAsync(s, AlertMessages.ApprovalsWaiting(null, 3), "tk_secret");
        Assert.True(ok);
        Assert.Equal("https://ntfy.example.com/", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("cars-abcdefghijkmnpqrstuvw", doc.RootElement.GetProperty("topic").GetString());
        Assert.Equal("C.A.R.S.: 3 admin items waiting", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task Refuses_bad_settings_and_reports_server_errors()
    {
        var handler = new Capture(HttpStatusCode.Forbidden);
        var client = new NtfyClient(new HttpClient(handler));
        Assert.False((await client.SendAsync(new AlertSettings { Topic = null }, AlertMessages.Test(null), null)).Ok);
        Assert.Null(handler.Request);
        var (ok, message) = await client.SendAsync(new AlertSettings { Topic = "cars-abcdefghijkmnpqrstuvw" }, AlertMessages.Test(null), null);
        Assert.False(ok);
        Assert.Contains("403", message);
    }

    [Fact]
    public void Tokens_are_encrypted_for_this_account()
    {
        var stored = Dpapi.Protect("tk_my_secret_token");
        Assert.NotNull(stored);
        Assert.DoesNotContain("tk_my_secret_token", stored);
        Assert.Equal("tk_my_secret_token", Dpapi.Unprotect(stored));
        Assert.Null(Dpapi.Unprotect("not base64!"));
        Assert.Null(Dpapi.Protect(""));
    }
}

public class CrashEventTests
{
    private const string Xml =
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Kernel-Power'/><EventID>41</EventID><TimeCreated SystemTime='2026-09-11T11:52:13.2180000Z'/></System><EventData><Data Name='BugcheckCode'>0</Data></EventData></Event>" +
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-WER-SystemErrorReporting'/><EventID>1001</EventID><TimeCreated SystemTime='2026-09-11T11:52:31.2070000Z'/></System></Event>" +
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Kernel-Power'/><EventID>41</EventID><TimeCreated SystemTime='2026-09-12T04:01:56.0730000Z'/></System><EventData><Data Name='BugcheckCode'>0</Data></EventData></Event>" +
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Kernel-Power'/><EventID>41</EventID><TimeCreated SystemTime='2026-10-03T17:27:51.6280000Z'/></System><EventData><Data Name='BugcheckCode'>278</Data></EventData></Event>";

    [Fact]
    public void A_blue_screen_and_its_power_event_are_one_incident()
    {
        var incidents = CrashEvents.Incidents(CrashEvents.Parse(Xml));
        Assert.Equal(3, incidents.Count);
        Assert.True(incidents[0].BlueScreen);   // 41 + WER 1001 eighteen seconds apart
        Assert.False(incidents[1].BlueScreen);  // 41 alone, no bug check: a freeze or power cut
        Assert.True(incidents[2].BlueScreen);   // 41 carrying bug check 0x116 (278)
    }

    [Fact]
    public void Empty_output_means_no_crashes() => Assert.Empty(CrashEvents.Parse(""));

    [Fact]
    public void The_query_is_for_the_two_crash_events_since_a_time()
    {
        var q = CrashEvents.Query(new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc));
        Assert.Contains("EventID=41", q);
        Assert.Contains("EventID=1001", q);
        Assert.Contains("2026-10-13T12:00:00.000Z", q);
    }
}
