using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace System.Runtime.CompilerServices
{
    // Lets netstandard2.0 / net48 compile C# 9+ records and init-only properties.
    internal static class IsExternalInit
    {
    }
}

namespace CarsControl.Setup
{
    internal static class Hex
    {
        public static string Of(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
                return Of(sha.ComputeHash(fs));
        }

        public static string Sha512File(string path)
        {
            using (var sha = SHA512.Create())
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
                return Of(sha.ComputeHash(fs));
        }

        public static string Sha256Stream(System.IO.Stream s)
        {
            using (var sha = SHA256.Create()) return Of(sha.ComputeHash(s));
        }

        public static bool SameHash(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
