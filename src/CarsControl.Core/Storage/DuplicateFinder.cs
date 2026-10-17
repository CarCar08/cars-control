using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace CarsControl.Core.Storage;

public sealed record DuplicateFile(string Path, long Bytes, DateTime Modified);

public sealed record DuplicateGroup(string Hash, long Bytes, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>What deleting all but one copy would free.</summary>
    public long Wasted => Bytes * (Files.Count - 1);
}

public sealed record DuplicateOptions(
    IReadOnlyList<string> Folders,
    long MinBytes = 1_048_576,
    Func<string, bool>? Skip = null,
    int Parallelism = 2);

public sealed record DuplicateProgress(string Stage, long FilesSeen, long Candidates, long Hashed);

/// <summary>
/// Finds files with identical content in the folders you choose. Matching goes size → the first and last 64 KB → a full
/// SHA-256, so only files that are very likely identical are ever read in full. Files that are the same file (hard links)
/// count once, online-only cloud files are never downloaded, and anything <see cref="DuplicateOptions.Skip"/> says is an
/// app's, a game's or a vault's own file is left out. Nothing is changed here: see <see cref="DuplicateRules"/>.
/// </summary>
public static class DuplicateFinder
{
    private const int Edge = 64 * 1024;
    private static readonly FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x400000 | (FileAttributes)0x40000;

    public static Task<IReadOnlyList<DuplicateGroup>> FindAsync(DuplicateOptions options, IProgress<DuplicateProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Find(options, progress, ct), ct);

    public static IReadOnlyList<DuplicateGroup> Find(DuplicateOptions o, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        // 1. Everything big enough, grouped by size.
        var bySize = new Dictionary<long, List<FileInfo>>();
        long seen = 0;
        foreach (var folder in o.Folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var f in Walk(folder, o.Skip, ct))
            {
                if (f.Length < o.MinBytes) continue;
                if (!bySize.TryGetValue(f.Length, out var list)) bySize[f.Length] = list = [];
                list.Add(f);
                if (++seen % 500 == 0) progress?.Report(new DuplicateProgress("Listing files", seen, 0, 0));
            }
        }

        // 2. Same size, then the same file seen twice (hard link) counts once.
        var sameSize = bySize.Where(kv => kv.Value.Count > 1).Select(kv => (Size: kv.Key, Files: DistinctFiles(kv.Value))).Where(g => g.Files.Count > 1).ToList();
        long candidates = sameSize.Sum(g => g.Files.Count);
        progress?.Report(new DuplicateProgress("Comparing", seen, candidates, 0));

        // 3. First and last 64 KB.
        var quick = new ConcurrentBag<(long Size, string Key, FileInfo File)>();
        long hashed = 0;
        Parallel.ForEach(sameSize.SelectMany(g => g.Files.Select(f => (g.Size, File: f))), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.Parallelism), CancellationToken = ct }, item =>
        {
            var key = EdgeKey(item.File, item.Size);
            if (key is not null) quick.Add((item.Size, key, item.File));
            var n = Interlocked.Increment(ref hashed);
            if (n % 50 == 0) progress?.Report(new DuplicateProgress("Comparing", seen, candidates, n));
        });
        var survivors = quick.GroupBy(q => (q.Size, q.Key)).Where(g => g.Count() > 1).ToList();

        // 4. Full hash of what's left.
        var hashedFiles = new ConcurrentBag<(long Size, string Hash, FileInfo File)>();
        long full = 0, fullTotal = survivors.Sum(g => g.Count());
        Parallel.ForEach(survivors.SelectMany(g => g), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.Parallelism), CancellationToken = ct }, q =>
        {
            ct.ThrowIfCancellationRequested();
            if (FullHash(q.File) is { } hash) hashedFiles.Add((q.Size, hash, q.File));
            var n = Interlocked.Increment(ref full);
            if (n % 5 == 0) progress?.Report(new DuplicateProgress("Reading to be sure", seen, fullTotal, n));
        });

        return hashedFiles.GroupBy(h => (h.Size, h.Hash)).Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(g.Key.Hash, g.Key.Size, g.Select(h => new DuplicateFile(h.File.FullName, h.File.Length, h.File.LastWriteTime)).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(g => g.Wasted).ToList();
    }

    private static IEnumerable<FileInfo> Walk(string root, Func<string, bool>? skip, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System, ReturnSpecialDirectories = false };
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = new DirectoryInfo(stack.Pop());
            List<DirectoryInfo> subs;
            List<FileInfo> files;
            try
            {
                subs = dir.EnumerateDirectories("*", options).ToList();
                files = dir.EnumerateFiles("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var s in subs)
                if (!s.Attributes.HasFlag(FileAttributes.ReparsePoint) && skip?.Invoke(s.FullName) != true) stack.Push(s.FullName);
            foreach (var f in files)
            {
                if (f.Attributes.HasFlag(FileAttributes.ReparsePoint) || (f.Attributes & CloudOnly) != 0) continue;
                if (skip?.Invoke(f.FullName) == true) continue;
                yield return f;
            }
        }
    }

    /// <summary>Hard links to one file show up as several paths: keep one.</summary>
    private static List<FileInfo> DistinctFiles(List<FileInfo> files)
    {
        var seen = new HashSet<(uint, uint, uint)>();
        var list = new List<FileInfo>(files.Count);
        foreach (var f in files)
        {
            if (FileId(f.FullName) is { } id && !seen.Add(id)) continue;
            list.Add(f);
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);

    private static (uint Volume, uint High, uint Low)? FileId(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
            return GetFileInformationByHandle(h, out var info) && info.NumberOfLinks > 1 ? (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? EdgeKey(FileInfo file, long size)
    {
        try
        {
            using var s = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            var buffer = new byte[Edge];
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var n = ReadFully(s, buffer);
            sha.AppendData(buffer, 0, n);
            if (size > Edge)
            {
                s.Seek(-Math.Min(Edge, size - Edge), SeekOrigin.End);
                n = ReadFully(s, buffer);
                sha.AppendData(buffer, 0, n);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FullHash(FileInfo file)
    {
        try
        {
            using var s = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(s));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int ReadFully(Stream s, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

public enum KeepRule { Newest, Oldest, ShortestPath, InFolder }

/// <summary>Which copy of a duplicate to keep. There is always exactly one kept, and it is never the one you'd lose by accident.</summary>
public static class DuplicateRules
{
    public static (DuplicateFile Keep, IReadOnlyList<DuplicateFile> Extras) Choose(DuplicateGroup g, KeepRule rule, string? folder = null)
    {
        IEnumerable<DuplicateFile> ordered = rule switch
        {
            KeepRule.Newest => g.Files.OrderByDescending(f => f.Modified).ThenBy(f => f.Path.Length),
            KeepRule.Oldest => g.Files.OrderBy(f => f.Modified).ThenBy(f => f.Path.Length),
            KeepRule.ShortestPath => g.Files.OrderBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase),
            _ => g.Files.OrderByDescending(f => folder is not null && IsIn(f.Path, folder)).ThenByDescending(f => f.Modified),
        };
        var list = ordered.ToList();
        return (list[0], list.Skip(1).ToList());
    }

    public static bool IsIn(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Before recycling: the file is still what was scanned (same size and time) and a kept copy still exists.</summary>
    public static bool StillSafeToRecycle(DuplicateFile extra, DuplicateFile keep)
    {
        try
        {
            var e = new FileInfo(extra.Path);
            var k = new FileInfo(keep.Path);
            return e.Exists && k.Exists && e.Length == extra.Bytes && e.LastWriteTime == extra.Modified && k.Length == keep.Bytes
                && !string.Equals(e.FullName, k.FullName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
