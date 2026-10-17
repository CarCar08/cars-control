using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CarsControl.Setup
{
    /// <summary>Finding out whether the .NET Desktop Runtime the app needs is installed, and where to
    /// download it from (Microsoft's own release metadata, with the SHA-512 it publishes for each file).</summary>
    public static class DotNetRuntime
    {
        public const string Product = "Microsoft.WindowsDesktop.App";

        /// <summary>The place Microsoft's installer puts the 64-bit runtimes.</summary>
        public static string DefaultRoot =>
            Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");

        /// <summary>Installed Desktop Runtime versions, read from the shared-framework folders.</summary>
        public static IReadOnlyList<SemVer> Installed(string? dotnetRoot = null)
        {
            var dir = Path.Combine(dotnetRoot ?? DefaultRoot, "shared", Product);
            var list = new List<SemVer>();
            if (!Directory.Exists(dir)) return list;
            foreach (var sub in Directory.GetDirectories(dir))
                if (SemVer.TryParse(Path.GetFileName(sub), out var v)) list.Add(v);
            return list.OrderBy(v => v).ToList();
        }

        /// <summary>"10.0" is satisfied by any 10.x with minor 0 or higher (roll-forward within the major is
        /// what the app's runtimeconfig allows). Preview builds don't count.</summary>
        public static bool IsSatisfied(string required, IEnumerable<SemVer> installed)
        {
            if (!SemVer.TryParse(required, out var need)) return false;
            return installed.Any(v => !v.IsPreRelease && v.Major == need.Major && v.Minor >= need.Minor);
        }

        public static bool IsSatisfied(string required, string? dotnetRoot = null) => IsSatisfied(required, Installed(dotnetRoot));

        /// <summary>Where Microsoft publishes release metadata for a channel ("10.0").</summary>
        public static string MetadataUrl(string channel) =>
            "https://builds.dotnet.microsoft.com/dotnet/release-metadata/" + channel + "/releases.json";

        private static readonly string[] AllowedHosts =
        {
            "builds.dotnet.microsoft.com", "dotnetcli.azureedge.net", "download.visualstudio.microsoft.com",
            "dotnetcli.blob.core.windows.net", "download.microsoft.com",
        };

        public sealed class Download
        {
            public string Version { get; set; } = "";
            public string Url { get; set; } = "";
            public string Sha512 { get; set; } = "";
            public string FileName { get; set; } = "";
        }

        /// <summary>The newest stable x64 Desktop Runtime installer in a releases.json document, or null.
        /// Only https links on Microsoft download hosts are accepted, and a hash must be present.</summary>
        public static Download? FindInstaller(string releasesJson)
        {
            using (var doc = JsonDocument.Parse(releasesJson))
            {
                if (!doc.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return null;
                Download? best = null;
                SemVer? bestVersion = null;
                foreach (var release in releases.EnumerateArray())
                {
                    if (!release.TryGetProperty("windowsdesktop", out var desktop) || desktop.ValueKind != JsonValueKind.Object) continue;
                    var version = Str(desktop, "version") ?? Str(release, "release-version");
                    if (version == null || !SemVer.TryParse(version, out var parsed) || parsed.IsPreRelease) continue;
                    if (bestVersion != null && parsed <= bestVersion) continue;
                    if (!desktop.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
                    foreach (var file in files.EnumerateArray())
                    {
                        var rid = Str(file, "rid");
                        var name = Str(file, "name") ?? "";
                        var url = Str(file, "url");
                        var hash = Str(file, "hash");
                        if (rid != "win-x64" || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(hash)) continue;
                        if (!IsAllowedUrl(url!)) continue;
                        best = new Download { Version = version, Url = url!, Sha512 = hash!, FileName = name };
                        bestVersion = parsed;
                    }
                }
                return best;
            }
        }

        public static bool IsAllowedUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// <summary>True when the file at <paramref name="path"/> has the SHA-512 Microsoft published.</summary>
        public static bool VerifyDownload(string path, string expectedSha512) => Hex.SameHash(Hex.Sha512File(path), expectedSha512);

        /// <summary>Arguments for Microsoft's installer: no window, no restart.</summary>
        public static string SilentArguments => "/install /quiet /norestart";
    }
}
