using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarsControl.Setup
{
    public sealed class PayloadFile
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
    }

    /// <summary>What the installer carries: the app's files with their hashes, its version and the .NET
    /// runtime it needs. Built at release time from the publish folder; checked after extraction so a
    /// damaged download or a full disk can't leave a half-installed app.</summary>
    public sealed class PayloadManifest
    {
        public string Version { get; set; } = "0.0.0";

        /// <summary>The .NET Desktop Runtime major.minor this build needs, e.g. "10.0".</summary>
        public string RequiredRuntime { get; set; } = "10.0";

        /// <summary>The program that starts the app, relative to the install folder.</summary>
        public string MainExe { get; set; } = "CarsControl.exe";

        public List<PayloadFile> Files { get; set; } = new List<PayloadFile>();

        public long TotalSize => Files.Sum(f => f.Size);

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        public static PayloadManifest FromJson(string json) =>
            JsonSerializer.Deserialize<PayloadManifest>(json, Json) ?? throw new InvalidDataException("Empty manifest.");

        /// <summary>Hashes every file under <paramref name="folder"/> (paths use forward slashes).</summary>
        public static PayloadManifest Build(string folder, string version, string requiredRuntime = "10.0", string mainExe = "CarsControl.exe")
        {
            var root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var m = new PayloadManifest { Version = version, RequiredRuntime = requiredRuntime, MainExe = mainExe };
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                m.Files.Add(new PayloadFile
                {
                    Path = file.Substring(root.Length).Replace('\\', '/'),
                    Sha256 = Hex.Sha256File(file),
                    Size = new FileInfo(file).Length,
                });
            }
            return m;
        }

        /// <summary>Problems found checking <paramref name="folder"/> against this manifest: missing, changed
        /// or unexpected files. Empty means it matches exactly.</summary>
        public IReadOnlyList<string> Verify(string folder)
        {
            var problems = new List<string>();
            var root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var expected = new HashSet<string>(Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var f in Files)
            {
                var full = Path.GetFullPath(Path.Combine(root, f.Path.Replace('/', '\\')));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { problems.Add("Unsafe path in the manifest: " + f.Path); continue; }
                if (!File.Exists(full)) { problems.Add("Missing: " + f.Path); continue; }
                if (new FileInfo(full).Length != f.Size || !Hex.SameHash(Hex.Sha256File(full), f.Sha256)) problems.Add("Damaged: " + f.Path);
            }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(root.Length).Replace('\\', '/');
                if (!expected.Contains(rel)) problems.Add("Unexpected: " + rel);
            }
            return problems;
        }
    }

    public static class SafeZip
    {
        /// <summary>Extracts every entry under <paramref name="target"/>, refusing any entry whose path would
        /// leave it ("zip slip": ..\ segments, rooted paths, drive letters, alternate streams).</summary>
        public static void Extract(Stream zip, string target, Action<int, string>? progress = null)
        {
            var root = Path.GetFullPath(target).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(root);
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
            {
                var files = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                for (var i = 0; i < files.Count; i++)
                {
                    var entry = files[i];
                    var name = entry.FullName.Replace('/', '\\');
                    if (name.Contains(":") || name.StartsWith("\\", StringComparison.Ordinal))
                        throw new InvalidDataException("Unsafe path in the package: " + entry.FullName);
                    var dest = Path.GetFullPath(Path.Combine(root, name));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unsafe path in the package: " + entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using (var src = entry.Open())
                    using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                        src.CopyTo(dst);
                    progress?.Invoke((i + 1) * 100 / files.Count, entry.FullName);
                }
            }
        }

        /// <summary>Zips a folder (used by the release build).</summary>
        public static void Create(string folder, string zipPath)
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            var root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var entry = archive.CreateEntry(file.Substring(root.Length).Replace('\\', '/'), CompressionLevel.Optimal);
                    using (var src = File.OpenRead(file))
                    using (var dst = entry.Open())
                        src.CopyTo(dst);
                }
            }
        }
    }
}
