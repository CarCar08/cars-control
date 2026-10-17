using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CarsControl.Setup
{
    public sealed class ReleaseAsset
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }

        /// <summary>The public download link.</summary>
        public string DownloadUrl { get; set; } = "";

        /// <summary>The API link: works with a token for a private repository.</summary>
        public string ApiUrl { get; set; } = "";
    }

    public sealed class ReleaseInfo
    {
        public SemVer Version { get; set; } = new SemVer(0, 0, 0);
        public string Tag { get; set; } = "";
        public string Title { get; set; } = "";
        public string Notes { get; set; } = "";
        public bool PreRelease { get; set; }
        public bool Draft { get; set; }
        public DateTime? Published { get; set; }
        public string PageUrl { get; set; } = "";
        public List<ReleaseAsset> Assets { get; set; } = new List<ReleaseAsset>();

        public const string SetupAssetPattern = @"^CarsControl-Setup-.+\.exe$";
        public const string ChecksumsAsset = "SHA256SUMS.txt";

        public ReleaseAsset? SetupAsset => Assets.FirstOrDefault(a => Regex.IsMatch(a.Name, SetupAssetPattern, RegexOptions.IgnoreCase));
        public ReleaseAsset? Checksums => Assets.FirstOrDefault(a => string.Equals(a.Name, ChecksumsAsset, StringComparison.OrdinalIgnoreCase));

        /// <summary>One release object from the GitHub API (GET /repos/{owner}/{repo}/releases/latest or an item of /releases).</summary>
        public static ReleaseInfo? FromGitHub(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            var tag = Str(e, "tag_name");
            if (tag == null || !SemVer.TryParse(tag, out var version)) return null;
            var info = new ReleaseInfo
            {
                Version = version,
                Tag = tag,
                Title = Str(e, "name") ?? tag,
                Notes = Str(e, "body") ?? "",
                PreRelease = Bool(e, "prerelease"),
                Draft = Bool(e, "draft"),
                PageUrl = Str(e, "html_url") ?? "",
            };
            if (DateTime.TryParse(Str(e, "published_at"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)) info.Published = when;
            if (e.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                    info.Assets.Add(new ReleaseAsset
                    {
                        Name = Str(a, "name") ?? "",
                        Size = a.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0,
                        DownloadUrl = Str(a, "browser_download_url") ?? "",
                        ApiUrl = Str(a, "url") ?? "",
                    });
            return info;
        }

        /// <summary>The newest release newer than <paramref name="current"/> that has a setup file and a checksum
        /// list. Pre-releases only count when <paramref name="includePreReleases"/> or you're already on one.</summary>
        public static ReleaseInfo? PickUpdate(string releasesJson, SemVer current, bool includePreReleases)
        {
            using (var doc = JsonDocument.Parse(releasesJson))
            {
                var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
                var wantPre = includePreReleases || current.IsPreRelease;
                return items.Select(FromGitHub)
                    .Where(r => r != null && !r.Draft && (wantPre || !r.PreRelease) && r.SetupAsset != null && r.Checksums != null && r.Version > current)
                    .OrderByDescending(r => r!.Version)
                    .FirstOrDefault();
            }
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }

    /// <summary>SHA256SUMS.txt in the usual "hash  filename" (or "hash *filename") form.</summary>
    public static class Checksums
    {
        public static Dictionary<string, string> Parse(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var m = Regex.Match(raw.Trim(), @"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>.+)$");
                if (m.Success) map[m.Groups["name"].Value.Trim()] = m.Groups["hash"].Value.ToLowerInvariant();
            }
            return map;
        }

        public static string? HashFor(string text, string fileName) =>
            Parse(text).TryGetValue(fileName, out var h) ? h : null;

        public static string Build(IEnumerable<KeyValuePair<string, string>> files) =>
            string.Join("\n", files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f => f.Value + "  " + f.Key)) + "\n";
    }
}
