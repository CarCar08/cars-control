using System.Diagnostics;
using System.IO.Enumeration;

namespace CarsControl.Core.Storage;

/// <summary>One directory in a scan. Children are directories only; files are summed into OwnFilesBytes.</summary>
public sealed class DirNode(string name, DirNode? parent)
{
    public string Name { get; } = name;
    public DirNode? Parent { get; } = parent;
    public List<DirNode> Children { get; } = [];
    public long OwnFilesBytes;
    public long OwnFileCount;
    public long TotalBytes;
    public long TotalFiles;
    public bool Inaccessible;

    /// <summary>Loaded from a saved scan that stopped at this depth: its subfolders exist but weren't
    /// kept. Drilling in rescans just this folder.</summary>
    public bool Truncated;

    public string FullPath => Parent is null ? Name : Path.Combine(Parent.FullPath, Name);

    /// <summary>For trees built from recursive totals (WizTree export, saved scans): derives each
    /// folder's own-file bytes from its total minus its children, then sorts biggest first.</summary>
    public void FinishFromTotals()
    {
        long childBytes = 0, childFiles = 0;
        foreach (var c in Children)
        {
            c.FinishFromTotals();
            childBytes += c.TotalBytes;
            childFiles += c.TotalFiles;
        }
        OwnFilesBytes = Math.Max(0, TotalBytes - childBytes);
        OwnFileCount = Math.Max(0, TotalFiles - childFiles);
        Children.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
    }

    /// <summary>Copies a subtree under a new parent (grafting a fresh subfolder scan into a saved tree).</summary>
    public DirNode CloneUnder(DirNode? newParent, string? name = null)
    {
        var copy = new DirNode(name ?? Name, newParent)
        {
            OwnFilesBytes = OwnFilesBytes, OwnFileCount = OwnFileCount, TotalBytes = TotalBytes,
            TotalFiles = TotalFiles, Inaccessible = Inaccessible, Truncated = Truncated,
        };
        foreach (var c in Children) copy.Children.Add(c.CloneUnder(copy));
        return copy;
    }

    internal void Finish()
    {
        long bytes = OwnFilesBytes, files = OwnFileCount;
        foreach (var c in Children)
        {
            c.Finish();
            bytes += c.TotalBytes;
            files += c.TotalFiles;
        }
        TotalBytes = bytes;
        TotalFiles = files;
        Children.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
    }
}

public sealed record BigFile(string Path, long Bytes, DateTime LastWrite)
{
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}

public sealed record ScanProgress(long Files, long Bytes, string CurrentPath);

public sealed class ScanResult
{
    public required DirNode Root { get; init; }
    public required IReadOnlyList<BigFile> Largest { get; init; }
    public required IReadOnlyList<BigFile> OldLarge { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required DateTime When { get; init; }
    public long DriveUsedBytes { get; init; }

    /// <summary>"Fast scan (MFT)" via WizTree, or "Folder scan".</summary>
    public string Method { get; init; } = "Folder scan";

    public bool FromCache { get; init; }

    /// <summary>Online-only cloud files (iCloud, Proton Drive, OneDrive): listed in folders at full size
    /// but not stored on this disk, so they're left out of every total.</summary>
    public long OnlineOnlyBytes { get; init; }

    /// <summary>Space the unelevated scan couldn't see: system files, other profiles, WindowsApps, pagefile.</summary>
    public long UnreadableBytes => Math.Max(0, DriveUsedBytes - Root.TotalBytes);
}

/// <summary>
/// Unelevated parallel folder scan. Skips links (junctions/symlinks would double-count or loop) and
/// online-only cloud files (not on disk), but walks cloud-sync folders, whose downloaded files are real.
/// Tracks the largest files and "big and stale" files on the fly so the Big &amp; old files view needs no
/// second pass.
/// </summary>
public static class FolderScanner
{
    public const int LargestCount = 2000;
    public static readonly long OldMinBytes = 50L * 1024 * 1024;
    public static readonly TimeSpan OldAge = TimeSpan.FromDays(180);

    /// <summary>maxDegree defaults to <see cref="DriveKind.ScanParallelism"/>: 1 thread on an HDD (A:),
    /// 6 on NVMe. Parallel enumeration on a spinning disk just makes it seek.</summary>
    public static Task<ScanResult> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken ct, int? maxDegree = null) =>
        Task.Run(() => Scan(rootPath, progress, ct, maxDegree ?? DriveKind.ScanParallelism(rootPath)), ct);

    private static ScanResult Scan(string rootPath, IProgress<ScanProgress>? progress, CancellationToken ct, int maxDegree)
    {
        var sw = Stopwatch.StartNew();
        var root = new DirNode(rootPath.TrimEnd('\\') + (rootPath.Length <= 3 ? "\\" : ""), null);
        var largest = new PriorityQueue<BigFile, long>();
        var old = new List<BigFile>();
        long threshold = 0, files = 0, bytes = 0, onlineOnly = 0;
        var gate = new object();
        var oldCutoff = DateTime.UtcNow - OldAge;
        var lastReport = Stopwatch.StartNew();

        void ScanDir(DirNode node)
        {
            var stack = new Stack<DirNode>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = stack.Pop();
                var path = current.FullPath;
                try
                {
                    var entries = new FileSystemEnumerable<(bool Dir, string Name, long Len, DateTime Write, FileAttributes Attr)>(
                        path,
                        (ref FileSystemEntry e) => (e.IsDirectory, e.FileName.ToString(), e.Length, e.LastWriteTimeUtc.UtcDateTime, e.Attributes),
                        new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 });

                    foreach (var e in entries)
                    {
                        if (e.Dir)
                        {
                            if (e.Attr.HasFlag(FileAttributes.ReparsePoint) && DirectorySizer.IsLink(System.IO.Path.Combine(path, e.Name))) continue;
                            var child = new DirNode(e.Name, current);
                            current.Children.Add(child);
                            stack.Push(child);
                            continue;
                        }
                        if (DirectorySizer.IsPlaceholder(e.Attr))
                        {
                            Interlocked.Add(ref onlineOnly, e.Len);
                            continue;
                        }
                        current.OwnFilesBytes += e.Len;
                        current.OwnFileCount++;
                        Interlocked.Increment(ref files);
                        Interlocked.Add(ref bytes, e.Len);

                        if (e.Len > Volatile.Read(ref threshold) || e.Len >= OldMinBytes)
                        {
                            lock (gate)
                            {
                                if (largest.Count < LargestCount || e.Len > threshold)
                                {
                                    var full = System.IO.Path.Combine(path, e.Name);
                                    largest.Enqueue(new BigFile(full, e.Len, e.Write.ToLocalTime()), e.Len);
                                    if (largest.Count > LargestCount) largest.Dequeue();
                                    if (largest.Count == LargestCount) Volatile.Write(ref threshold, largest.Peek().Bytes);
                                }
                                if (e.Len >= OldMinBytes && e.Write < oldCutoff && old.Count < 5000)
                                    old.Add(new BigFile(System.IO.Path.Combine(path, e.Name), e.Len, e.Write.ToLocalTime()));
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    current.Inaccessible = true;
                }

                if (progress is not null && lastReport.ElapsedMilliseconds > 250)
                {
                    lastReport.Restart();
                    progress.Report(new ScanProgress(Interlocked.Read(ref files), Interlocked.Read(ref bytes), path));
                }
            }
        }

        // Read the top level first, then fan out across its subfolders.
        try
        {
            var top = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (var d in new DirectoryInfo(root.FullPath).EnumerateDirectories("*", top))
            {
                if (d.Attributes.HasFlag(FileAttributes.ReparsePoint) && DirectorySizer.IsLink(d.FullName)) continue;
                root.Children.Add(new DirNode(d.Name, root));
            }
            foreach (var f in new DirectoryInfo(root.FullPath).EnumerateFiles("*", top))
            {
                if (DirectorySizer.IsPlaceholder(f.Attributes))
                {
                    onlineOnly += f.Length;
                    continue;
                }
                root.OwnFilesBytes += f.Length;
                root.OwnFileCount++;
            }
        }
        catch (UnauthorizedAccessException)
        {
            root.Inaccessible = true;
        }

        Parallel.ForEach(root.Children, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxDegree), CancellationToken = ct }, ScanDir);
        root.Finish();

        long used = 0;
        try
        {
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(root.FullPath)!);
            if (string.Equals(drive.RootDirectory.FullName.TrimEnd('\\'), root.FullPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                used = drive.TotalSize - drive.TotalFreeSpace;
        }
        catch
        {
            // Not a drive root.
        }

        return new ScanResult
        {
            Root = root,
            Largest = largest.UnorderedItems.Select(x => x.Element).OrderByDescending(f => f.Bytes).ToList(),
            OldLarge = old.OrderByDescending(f => f.Bytes).ToList(),
            Elapsed = sw.Elapsed,
            When = DateTime.Now,
            DriveUsedBytes = used,
            OnlineOnlyBytes = onlineOnly,
        };
    }
}
