using System.Globalization;
using System.Text.RegularExpressions;

namespace CarsControl.Core.Health;

/// <summary>A blue screen: when, what Windows called it, and the dump it left.</summary>
public sealed record BlueScreen(DateTime At, uint Code, string Name, IReadOnlyList<string> Parameters, string? DumpPath, string Summary);

/// <summary>The names Windows gives the stop codes people actually meet. Unknown codes show as hex.</summary>
public static partial class BugChecks
{
    private static readonly Dictionary<uint, (string Name, string Meaning)> Known = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", "a driver touched memory it shouldn't at the wrong priority level"),
        [0x1A] = ("MEMORY_MANAGEMENT", "Windows' memory manager found something badly wrong (bad RAM and drivers are the usual causes)"),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", "kernel code hit an error nothing handled"),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "an error while switching from a program into the kernel (often a graphics or antivirus driver)"),
        [0x4E] = ("PFN_LIST_CORRUPT", "the memory page list was corrupted (a driver or failing RAM)"),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "something used memory that isn't there (a driver, or failing RAM)"),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "a system thread hit an error nothing handled (usually a driver)"),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "a driver didn't finish a power change (sleep, wake, shut down) in time"),
        [0xC2] = ("BAD_POOL_CALLER", "a driver misused kernel memory"),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "a driver touched memory it shouldn't at the wrong priority level"),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "a process Windows can't run without ended"),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", "a process Windows can't run without was terminated (often a failing drive)"),
        [0x113] = ("VIDEO_DXGKRNL_FATAL_ERROR", "the graphics kernel (dxgkrnl) hit a fatal error: graphics driver or GPU"),
        [0x116] = ("VIDEO_TDR_FAILURE", "the graphics driver stopped responding and couldn't recover"),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "the graphics driver took too long and Windows gave up waiting"),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "the graphics scheduler found an internal error: GPU or graphics driver"),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "the hardware reported an error it couldn't correct (CPU, memory, motherboard, or overheating/power)"),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "a driver held the CPU too long without letting anything else run"),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "kernel code found corrupted data (drivers or memory)"),
        [0x14C] = ("FATAL_ABNORMAL_RESET_ERROR", "the PC was reset abnormally by hardware or firmware"),
        [0x1000007E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED_M", "a system thread hit an error nothing handled"),
    };

    public static string NameOf(uint code) => Known.TryGetValue(code, out var k) ? k.Name : $"0x{code:X}";

    public static string MeaningOf(uint code) => Known.TryGetValue(code, out var k) ? k.Meaning : "Windows stopped because of a serious error";

    /// <summary>"The bugcheck was: 0x0000003b (0x00000000c0000005, 0xfffff8056e16ca26, …). A dump was saved in: C:\Windows\MEMORY.DMP."</summary>
    public static BlueScreen? FromReport(DateTime at, string? message)
    {
        if (message is null) return null;
        var m = Report().Match(message);
        if (!m.Success) return null;
        var code = uint.Parse(m.Groups["code"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var ps = m.Groups["params"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var dump = Dump().Match(message) is { Success: true } d ? d.Groups["path"].Value : null;
        return new BlueScreen(at, code, NameOf(code), ps, dump, MeaningOf(code));
    }

    [GeneratedRegex(@"bugcheck was:\s*0x(?<code>[0-9a-fA-F]+)\s*\((?<params>[^)]*)\)")]
    private static partial Regex Report();

    [GeneratedRegex(@"dump was saved in:\s*(?<path>.+?\.dmp)", RegexOptions.IgnoreCase)]
    private static partial Regex Dump();
}

/// <summary>A minidump on disk. The folder can be listed without admin, but the files themselves can only be read by
/// administrators, so the helper copies one for the debugger (<see cref="DumpFiles.IsValidName"/>).</summary>
public sealed record DumpFile(string Path, DateTime Written, long Bytes)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public static class DumpFiles
{
    public static string MinidumpFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");

    /// <summary>The largest dump the helper copies (minidumps are a few MB; anything bigger isn't a minidump).</summary>
    public const long MaxCopyBytes = 256L * 1024 * 1024;

    /// <summary>What Windows names a minidump: "091126-13359-01.dmp" (date, a counter, a sequence number). Nothing else is copied.</summary>
    public static bool IsValidName(string? name) =>
        name is not null && Regex.IsMatch(name, @"^\d{6}-\d{1,10}-\d{2}\.dmp$", RegexOptions.CultureInvariant);

    /// <summary>True when this account can open the file (it can't, for dumps in C:\Windows\Minidump).</summary>
    public static bool CanRead(string path)
    {
        try
        {
            using var _ = File.OpenRead(path);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static IReadOnlyList<DumpFile> List(string? folder = null)
    {
        folder ??= MinidumpFolder;
        try
        {
            return Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles("*.dmp").OrderByDescending(f => f.LastWriteTime).Select(f => new DumpFile(f.FullName, f.LastWriteTime, f.Length)).ToList()
                : [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>What the debugger concluded about one dump.</summary>
public sealed record DumpAnalysis(
    string Dump,
    uint? Code,
    string? Name,
    string? BlamedModule,
    string? ImageName,
    string? FailureBucket,
    string? ProcessName,
    string? Description,
    IReadOnlyList<string> StackTop)
{
    /// <summary>"nvlddmkm.sys" or "ntkrnlmp.exe" (Windows itself, usually a symptom, not the cause).</summary>
    public string? Blamed => ImageName ?? BlamedModule;

    public bool BlamesWindowsItself => Blamed is { } b && (b.StartsWith("nt", StringComparison.OrdinalIgnoreCase) || b.Equals("hal.dll", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads the text WinDbg's <c>cdb -z dump -c "!analyze -v; q"</c> prints.</summary>
public static partial class DumpAnalyzer
{
    public static string AnalyzeCommand => "!analyze -v; q";

    public static DumpAnalysis Parse(string dump, IEnumerable<string> lines)
    {
        var all = lines.ToList();
        string? Field(string key) =>
            all.Select(l => Regex.Match(l, @"^\s*" + Regex.Escape(key) + @":\s*(?<v>.+?)\s*$")).Where(m => m.Success).Select(m => m.Groups["v"].Value).FirstOrDefault();

        uint? code = null;
        if (all.Select(l => Regex.Match(l, @"^\s*(?<name>[A-Z0-9_]+)\s+\((?<hex>[0-9a-fA-F]+)\)\s*$")).FirstOrDefault(m => m.Success) is { } header)
            code = uint.Parse(header.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        else if (Field("BUGCHECK_CODE") is { } bc && uint.TryParse(bc.Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)) code = parsed;

        // The paragraph under the header ("An exception happened while …").
        var desc = all.SkipWhile(l => !Regex.IsMatch(l, @"^\s*[A-Z0-9_]+\s+\([0-9a-fA-F]+\)\s*$")).Skip(1)
            .TakeWhile(l => !l.TrimStart().StartsWith("Arguments", StringComparison.OrdinalIgnoreCase)).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var stackStart = all.FindIndex(l => l.TrimStart().StartsWith("STACK_TEXT:", StringComparison.OrdinalIgnoreCase));
        var stack = stackStart < 0 ? [] : all.Skip(stackStart + 1).Take(40).TakeWhile(l => l.Trim().Length > 0)
            .Select(l => Regex.Replace(l.Trim(), @"^[0-9a-fA-F`]+\s+[0-9a-fA-F`]+\s+", "")).Take(8).ToList();

        var blamed = all.Select(l => Regex.Match(l, @"Probably caused by\s*:\s*(?<m>[^\s(]+)")).FirstOrDefault(m => m.Success)?.Groups["m"].Value;

        return new DumpAnalysis(dump, code, code is { } c ? BugChecks.NameOf(c) : null, Field("MODULE_NAME") ?? blamed, Field("IMAGE_NAME"),
            Field("FAILURE_BUCKET_ID"), Field("PROCESS_NAME"), desc.Count > 0 ? string.Join(" ", desc) : null, stack);
    }

    /// <summary>
    /// When the debugger produced no analysis, the line that says why ("Could not open dump file…, Win32 error 0n5"), so the
    /// app can show the real reason instead of "couldn't read this dump".
    /// </summary>
    public static string? ProblemIn(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.Contains("Could not open dump file", StringComparison.OrdinalIgnoreCase)) return l.Contains("0n5") || l.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                ? "Windows didn't let the debugger open the dump (access denied)." : l;
            if (l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) || l.Contains("is not a valid dump", StringComparison.OrdinalIgnoreCase)
                || l.Contains("dump file is corrupt", StringComparison.OrdinalIgnoreCase)) return l;
        }
        return null;
    }

    /// <summary>Where the debugger lives: the Store/winget package, or the classic Windows SDK location.</summary>
    public static string? FindCdb()
    {
        var candidates = new List<string>
        {
            @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe",
            @"C:\Program Files\Windows Kits\10\Debuggers\x64\cdb.exe",
        };
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        candidates.Add(Path.Combine(apps, "cdbX64.exe"));
        candidates.Add(Path.Combine(apps, "cdb.exe"));
        var packages = @"C:\Program Files\WindowsApps";
        try
        {
            if (Directory.Exists(packages))
                foreach (var dir in Directory.EnumerateDirectories(packages, "Microsoft.WinDbg_*"))
                    candidates.AddRange([Path.Combine(dir, "amd64", "cdb.exe"), Path.Combine(dir, "x64", "cdb.exe")]);
        }
        catch (UnauthorizedAccessException)
        {
            // The package folder isn't listable for standard users; the alias in WindowsApps still works.
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
