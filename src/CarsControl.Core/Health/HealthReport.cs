using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CarsControl.Core.Health;

public enum FindingLevel { Info, Watch, Bad }

/// <summary>One thing the Health page wants you to know, worded for a person.</summary>
public sealed record HealthFinding(FindingLevel Level, string Title, string Detail, string Area);

public sealed record HealthSnapshot(
    DateTime At,
    SystemSpecs? Specs = null,
    IReadOnlyList<DriveReading>? Drives = null,
    IReadOnlyList<GpuReading>? Gpus = null,
    IReadOnlyList<SensorReading>? Sensors = null,
    IReadOnlyList<BlueScreen>? BlueScreens = null,
    IReadOnlyList<DumpAnalysis>? Analyses = null,
    SleepReport? Sleep = null,
    BootReport? Boot = null,
    VpnStatus? Vpn = null,
    TailscaleState? Tailscale = null,
    PendingRebootState? Reboot = null,
    WindowsUpdateState? WindowsUpdate = null,
    string? AppVersion = null);

/// <summary>Turns the raw readings into the short list of things worth attention.</summary>
public static class HealthSummary
{
    public static IReadOnlyList<HealthFinding> Build(HealthSnapshot s, int cpuAlertC = 90, int gpuAlertC = 85)
    {
        var list = new List<HealthFinding>();

        foreach (var d in s.Drives ?? [])
        {
            var v = DriveHealthRules.Evaluate(d);
            if (v.Level != DriveLevel.Good)
                list.Add(new HealthFinding(v.Level == DriveLevel.Bad ? FindingLevel.Bad : FindingLevel.Watch, $"{d.Name} ({d.LettersText}): {v.Summary.ToLowerInvariant()}", string.Join(" ", v.Notes), "Drives"));
        }

        foreach (var g in s.Gpus ?? [])
            if (g.TempC is { } t && t >= gpuAlertC)
                list.Add(new HealthFinding(FindingLevel.Bad, $"{g.Name} is at {t} °C", $"The graphics card is hotter than the {gpuAlertC} °C you set. Check the fans and the case airflow.", "Sensors"));

        foreach (var c in (s.Sensors ?? []).Where(x => x.Type == "Temperature" && x.HardwareKind == "Cpu" && (x.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))))
            if (c.Value is { } t && t >= cpuAlertC)
                list.Add(new HealthFinding(FindingLevel.Bad, $"The processor is at {t:0} °C", $"That's above the {cpuAlertC} °C you set. Check the cooler and the case airflow.", "Sensors"));

        var recent = (s.BlueScreens ?? []).Where(b => b.At > s.At.AddDays(-14)).ToList();
        if (recent.Count > 0)
        {
            var kinds = recent.GroupBy(b => b.Name).OrderByDescending(g => g.Count()).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
            list.Add(new HealthFinding(recent.Count >= 3 ? FindingLevel.Bad : FindingLevel.Watch,
                $"{recent.Count} blue screen{(recent.Count == 1 ? "" : "s")} in the last 14 days", string.Join(", ", kinds) + ". The Crashes tab shows when and what Windows blamed.", "Crashes"));
        }

        if (s.Sleep is { } sleep)
        {
            var stalls = sleep.Stalls.Where(x => x.At > s.At.AddDays(-14)).ToList();
            if (stalls.Count > 0)
                list.Add(new HealthFinding(FindingLevel.Watch, $"The PC froze {stalls.Count} time{(stalls.Count == 1 ? "" : "s")} in the last 14 days",
                    $"The longest was {stalls.Max(x => x.Length).TotalSeconds:0} s (the system clock stopped, then caught up).", "Sleep & boot"));
            if (sleep.Headline is { } headline) list.Add(new HealthFinding(FindingLevel.Info, "Waking from sleep is slow", headline, "Sleep & boot"));
        }

        if (s.Boot?.Boots.FirstOrDefault() is { IsSlow: true } slow)
            list.Add(new HealthFinding(FindingLevel.Info, $"The last start-up took {slow.Boot.TotalSeconds:0} s", "The Sleep & boot tab shows which apps and drivers slowed it.", "Sleep & boot"));

        if (s.Reboot is { Pending: true } reboot)
            list.Add(new HealthFinding(FindingLevel.Watch, "A restart is waiting", reboot.Summary, "Overview"));

        if (s.Vpn is { Level: not VpnLevel.Carrying } vpn)
            list.Add(new HealthFinding(vpn.Level == VpnLevel.ConnectedNotCarrying ? FindingLevel.Watch : FindingLevel.Info, vpn.Level == VpnLevel.NotConnected ? "Proton VPN isn't connected" : "Proton VPN isn't carrying your traffic",
                vpn.Level == VpnLevel.NotConnected ? "Your traffic leaves through your normal connection, not the VPN." : vpn.Summary, "Network"));

        if (s.Tailscale is { Installed: true, Running: false } ts)
            list.Add(new HealthFinding(FindingLevel.Watch, "Tailscale isn't running", ts.Error ?? $"Its state is {ts.BackendState ?? "unknown"}.", "Network"));

        return list.OrderByDescending(f => f.Level).ThenBy(f => f.Area, StringComparer.Ordinal).ToList();
    }
}

/// <summary>A self-contained, shareable HTML page about this PC's health. Personal details are replaced when <c>redact</c> is on.</summary>
public static class HealthReport
{
    public static string Render(HealthSnapshot s, bool redact = true, string? userName = null, string? machineName = null)
    {
        var user = userName ?? Environment.UserName;
        var machine = machineName ?? s.Specs?.ComputerName ?? Environment.MachineName;
        string T(string? text) => WebUtility.HtmlEncode(redact ? Redact(text ?? "", user, machine) : text ?? "");

        var sb = new StringBuilder();
        void Row(string head, string valueHtml) => sb.Append("<tr><th>").Append(T(head)).Append("</th><td>").Append(valueHtml).Append("</td></tr>");
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>PC health report</title><style>")
          .Append(":root{--bg:#fff;--fg:#1c2330;--muted:#5f6b7a;--card:#f4f6f8;--line:#dde2e8;--good:#1a7f4b;--watch:#a86a00;--bad:#b3261e;--accent:#18a08c}")
          .Append("@media (prefers-color-scheme:dark){:root{--bg:#12161c;--fg:#e6e9ee;--muted:#98a2b0;--card:#1b212a;--line:#2b3340;--good:#4cc38a;--watch:#e0a63a;--bad:#ff6b61;--accent:#34c2ae}}")
          .Append("body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,Segoe UI,sans-serif}main{max-width:860px;margin:0 auto;padding:24px 16px 48px}")
          .Append("h1{font-size:24px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px;border-bottom:1px solid var(--line);padding-bottom:4px}p.sub{color:var(--muted);margin:0}")
          .Append("table{border-collapse:collapse;width:100%}td,th{text-align:left;padding:5px 8px;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--muted);font-weight:600;width:34%}")
          .Append(".f{background:var(--card);border-left:4px solid var(--muted);padding:8px 12px;margin:6px 0;border-radius:4px}.f.Bad{border-color:var(--bad)}.f.Watch{border-color:var(--watch)}.f.Info{border-color:var(--accent)}")
          .Append(".f b{display:block}.ok{color:var(--good)}.w{color:var(--watch)}.b{color:var(--bad)}footer{margin-top:32px;color:var(--muted);font-size:13px}")
          .Append("</style></head><body><main>");

        sb.Append("<h1>PC health report</h1><p class=\"sub\">").Append(T($"{machine} · made {s.At:yyyy-MM-dd HH:mm}"));
        if (s.AppVersion is not null) sb.Append(T($" · C.A.R.S. Control {s.AppVersion}"));
        sb.Append(redact ? " · personal details removed" : "").Append("</p>");

        var findings = HealthSummary.Build(s);
        sb.Append("<h2>What needs attention</h2>");
        if (findings.Count == 0) sb.Append("<p class=\"ok\">Nothing needs attention.</p>");
        foreach (var f in findings)
            sb.Append($"<div class=\"f {f.Level}\"><b>{T(f.Title)}</b>{T(f.Detail)}</div>");

        if (s.Specs is { } sp)
        {
            sb.Append("<h2>This PC</h2><table>");
            Row("Processor", T($"{sp.Cpu} ({sp.Cores} cores, {sp.Threads} threads)"));
            foreach (var g in sp.Gpus) Row("Graphics", T($"{g.Name}, driver {g.DriverVersion}" + (g.VramBytes > 0 ? $", {g.VramBytes / (1024.0 * 1024 * 1024):0.#} GB" : "")));
            Row("Memory", T(sp.RamSummary));
            Row("Motherboard", T($"{sp.BoardMaker} {sp.Board}"));
            Row("BIOS", T($"{sp.BiosVersion}" + (sp.BiosDate is { } bd ? $" ({bd:yyyy-MM-dd})" : "")));
            Row("Windows", T($"{sp.WindowsEdition} {sp.WindowsVersion} (build {sp.WindowsBuild})"));
            Row("Up for", T($"{(int)sp.Uptime.TotalDays} days {sp.Uptime.Hours} h"));
            sb.Append("</table>");
        }

        if (s.Drives is { Count: > 0 } drives)
        {
            sb.Append("<h2>Drives</h2><table>");
            foreach (var d in drives)
            {
                var v = DriveHealthRules.Evaluate(d);
                var detail = $"{d.Kind} · {d.SizeBytes / 1_000_000_000.0:0} GB · {d.LettersText}" + (d.TempC is { } t ? $" · {t} °C" : "") + (d.WearPercent is { } w ? $" · {w}% worn" : "") + (d.PowerOnHours is { } h ? $" · {h:N0} h on" : "");
                sb.Append($"<tr><th>{T(d.Name)}</th><td><span class=\"{(v.Level == DriveLevel.Good ? "ok" : v.Level == DriveLevel.Watch ? "w" : "b")}\">{T(v.Summary)}</span> — {T(detail)}</td></tr>");
            }
            sb.Append("</table>");
        }

        var temps = (s.Sensors ?? []).Where(x => x.Type == "Temperature" && x.Value is not null).Take(14).ToList();
        if (s.Gpus is { Count: > 0 } || temps.Count > 0)
        {
            sb.Append("<h2>Temperatures</h2><table>");
            foreach (var g in s.Gpus ?? []) Row(g.Name, T(g.TempC is { } gt ? $"{gt} °C" + (g.GpuLoadPct is { } gl ? $" at {gl}% load" : "") : "not available"));
            foreach (var c in temps) Row($"{c.Hardware}: {c.Name}", T($"{c.Value:0} °C"));
            sb.Append("</table>");
        }

        if (s.BlueScreens is { Count: > 0 } bsods)
        {
            sb.Append("<h2>Blue screens</h2><table>");
            foreach (var b in bsods.OrderByDescending(x => x.At).Take(12))
            {
                var analysis = s.Analyses?.FirstOrDefault(a => b.DumpPath is not null && string.Equals(a.Dump, b.DumpPath, StringComparison.OrdinalIgnoreCase));
                Row(b.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), T($"{b.Name}" + (analysis?.Blamed is { } m ? $" — blamed {m}" : "")));
            }
            sb.Append("</table>");
        }

        if (s.Sleep is { } sl && (sl.Cycles.Count > 0 || sl.Stalls.Count > 0))
        {
            sb.Append("<h2>Sleep, wake and freezes</h2><table>");
            Row("Sleeps recorded", T(sl.Cycles.Count.ToString(CultureInfo.InvariantCulture)));
            Row("Slow resumes (15 s or more)", T(sl.SlowResumes.ToString(CultureInfo.InvariantCulture)));
            Row("Freezes (clock stopped 3 s or more)", T(sl.Stalls.Count.ToString(CultureInfo.InvariantCulture)));
            if (sl.Headline is { } hl) Row("Slowest resume", T(hl));
            sb.Append("</table>");
        }

        if (s.Vpn is not null || s.Tailscale is { Installed: true })
        {
            sb.Append("<h2>Network</h2><table>");
            if (s.Vpn is { } v2) Row("Proton VPN", T(v2.Summary));
            if (s.Tailscale is { Installed: true } ts)
            {
                Row("Tailscale", T(ts.Running ? $"running, {ts.Peers.Count(p => p.Online)} of {ts.Peers.Count} devices online" : $"{ts.BackendState ?? "stopped"}"));
                foreach (var p in ts.Peers.Where(x => x.Online)) Row(p.Name, T(p.PathText));
            }
            sb.Append("</table>");
        }

        sb.Append("<footer>Made by C.A.R.S. Control on this PC. Nothing in this file was sent anywhere.</footer></main></body></html>");
        return sb.ToString();
    }


    /// <summary>Replaces your Windows user name, the PC's name and user-profile paths.</summary>
    public static string Redact(string text, string user, string machine)
    {
        if (text.Length == 0) return text;
        var result = Regex.Replace(text, @"(?i)[A-Z]:\\Users\\[^\\\s""<>|]+", @"C:\Users\(you)");
        if (user.Length >= 2) result = Regex.Replace(result, @"(?i)\b" + Regex.Escape(user) + @"\b", "(you)");
        if (machine.Length >= 2) result = Regex.Replace(result, @"(?i)\b" + Regex.Escape(machine) + @"\b", "(this PC)");
        return result;
    }
}
