using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CarsControl.Core.Maintenance;
using CarsControl.Core.Settings;

namespace CarsControl.Core.Alerts;

/// <summary>When an alert may go out: everything is held during quiet hours except critical ones.</summary>
public static class AlertPolicy
{
    public static bool InQuietHours(AlertSettings s, DateTime now)
    {
        if (!s.QuietHours
            || !MaintenanceSchedule.TryParseTime(s.QuietFrom, out var from)
            || !MaintenanceSchedule.TryParseTime(s.QuietTo, out var to)
            || from == to) return false;
        var t = now.TimeOfDay;
        return from < to ? t >= from && t < to : t >= from || t < to; // 23:00–08:00 wraps midnight
    }

    public static bool SendNow(Alert alert, AlertSettings s, DateTime now) => alert.Critical || !InQuietHours(s, now);

    /// <summary>Whether the event's switch in Settings › Phone alerts is on.</summary>
    public static bool Wanted(Alert alert, AlertSettings s) => alert.Kind switch
    {
        "test" => true,
        "maintenance" => s.MaintenanceSummary,
        "maintenance-failed" => s.Failures || s.MaintenanceSummary,
        "approval" => s.ApprovalWaiting,
        "low-disk" => s.LowDisk,
        "filling-fast" => s.DriveFillingFast,
        "bluescreen" or "shutdown" => s.Crashes,
        "drive-health" => s.DriveHealth,
        "temperature" => s.Temperature,
        "digest" => s.WeeklyDigest,
        _ => s.Failures,
    };

    /// <summary>A topic nobody can guess (the topic is the only key on a public ntfy server).</summary>
    public static string NewTopic()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789"; // no 0/o/1/l: easy to read off a screen
        var bytes = RandomNumberGenerator.GetBytes(22);
        return "cars-" + new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    public static bool IsValidTopic(string? topic) =>
        topic is { Length: >= 8 and <= 64 } && topic.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool IsValidServer(string? server) =>
        Uri.TryCreate(server, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.Query);
}

/// <summary>Publishes to an ntfy server (ntfy.sh or your own) with its JSON API.</summary>
public sealed class NtfyClient(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<(bool Ok, string Message)> SendAsync(AlertSettings s, Alert alert, string? token, CancellationToken ct = default)
    {
        if (!AlertPolicy.IsValidServer(s.Server)) return (false, "The server must be an https:// address.");
        if (!AlertPolicy.IsValidTopic(s.Topic)) return (false, "No valid topic yet.");
        var body = new
        {
            topic = s.Topic,
            title = alert.Title,
            message = alert.Message,
            priority = Math.Clamp(alert.Priority, 1, 5),
            tags = alert.Tags ?? [],
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, s.Server.TrimEnd('/') + "/") { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new("Bearer", token);
        try
        {
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode
                ? (true, "Sent.")
                : (false, $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Couldn't reach the server ({ex.HttpRequestError}).");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "The server didn't answer in time.");
        }
    }
}

/// <summary>Encrypts a secret for this Windows account (DPAPI), so the ntfy token isn't stored in plain
/// text in settings.json. Another account or PC can't decrypt it; there it's simply re-entered.</summary>
public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private const int UiForbidden = 0x1;

    public static string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        var bytes = Transform(Encoding.UTF8.GetBytes(secret), protect: true);
        return bytes is null ? null : Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            var bytes = Transform(Convert.FromBase64String(stored), protect: false);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[]? Transform(byte[] data, bool protect)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref input, "C.A.R.S. Control", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) return null;
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
