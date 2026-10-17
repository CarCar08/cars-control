using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CarsControl.Setup
{
    /// <summary>A version like 0.8.0 or 1.2.3-rc.1 (a leading "v" is fine). Pre-releases sort before the
    /// release they lead up to: 0.8.0-rc.1 &lt; 0.8.0.</summary>
    public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
    {
        private static readonly Regex Pattern = new Regex(
            @"^v?(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?(?:\.\d+)?(?:-(?<pre>[0-9A-Za-z.\-]+))?(?:\+[0-9A-Za-z.\-]+)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        public string? Pre { get; }
        public bool IsPreRelease => Pre != null;

        public SemVer(int major, int minor, int patch, string? pre = null)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Pre = string.IsNullOrEmpty(pre) ? null : pre;
        }

        public static bool TryParse(string? text, out SemVer version)
        {
            version = new SemVer(0, 0, 0);
            if (text == null) return false;
            var m = Pattern.Match(text.Trim());
            if (!m.Success) return false;
            if (!int.TryParse(m.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                || !int.TryParse(m.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return false;
            var patch = 0;
            if (m.Groups["patch"].Success && !int.TryParse(m.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch)) return false;
            version = new SemVer(major, minor, patch, m.Groups["pre"].Success ? m.Groups["pre"].Value : null);
            return true;
        }

        public static SemVer Parse(string text) =>
            TryParse(text, out var v) ? v : throw new FormatException("Not a version: " + text);

        public int CompareTo(SemVer? other)
        {
            if (other == null) return 1;
            var c = Major.CompareTo(other.Major);
            if (c != 0) return c;
            c = Minor.CompareTo(other.Minor);
            if (c != 0) return c;
            c = Patch.CompareTo(other.Patch);
            if (c != 0) return c;
            if (Pre == null) return other.Pre == null ? 0 : 1;
            if (other.Pre == null) return -1;
            return ComparePre(Pre, other.Pre);
        }

        /// <summary>Dot-separated identifiers: numbers compare as numbers and sort before words ("rc.2" &lt; "rc.10").</summary>
        private static int ComparePre(string a, string b)
        {
            var x = a.Split('.');
            var y = b.Split('.');
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                var xn = long.TryParse(x[i], NumberStyles.None, CultureInfo.InvariantCulture, out var nx);
                var yn = long.TryParse(y[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ny);
                int c;
                if (xn && yn) c = nx.CompareTo(ny);
                else if (xn) c = -1;
                else if (yn) c = 1;
                else c = string.CompareOrdinal(x[i], y[i]);
                if (c != 0) return c;
            }
            return x.Length.CompareTo(y.Length);
        }

        public bool Equals(SemVer? other) => other != null && CompareTo(other) == 0;
        public override bool Equals(object? obj) => Equals(obj as SemVer);
        public override int GetHashCode() => (Major * 397 ^ Minor) * 397 ^ Patch;
        public override string ToString() => Major + "." + Minor + "." + Patch + (Pre != null ? "-" + Pre : "");

        public static bool operator ==(SemVer? a, SemVer? b) => ReferenceEquals(a, b) || (a is not null && b is not null && a.CompareTo(b) == 0);
        public static bool operator !=(SemVer? a, SemVer? b) => !(a == b);
        public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
        public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
        public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
        public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    }
}
