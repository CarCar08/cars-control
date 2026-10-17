using System.Collections.ObjectModel;
using CarsControl.Core.Health;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CarsControl.App.ViewModels;

public enum HealthTab { Overview, Sensors, Drives, Crashes, SleepBoot, Drivers, Activity, Network }

/// <summary>A label and value line (specs, details).</summary>
public sealed record InfoRow(string Label, string Value)
{
    public override string ToString() => $"{Label}: {Value}";
}

/// <summary>One thing that needs attention. Level is "Bad", "Watch" or "Info" (the styles key off it).</summary>
public sealed record FindingRow(string Level, string Title, string Detail, string Area, HealthTab Tab)
{
    public override string ToString() => $"{Title}. {Detail}";
}

public sealed record GpuRow(string Name, string Driver, int? TempC, int? LoadPercent, string Details, string Power, string Memory, int? MemoryPercent)
{
    public string TempText => TempC is { } t ? $"{t} °C" : "—";
    public string LoadText => LoadPercent is { } l ? $"{l}%" : "—";
    public override string ToString() => $"{Name}, {TempText}, load {LoadText}";
}

public sealed record SensorRow(string Name, string Value, string Range, string Type)
{
    public override string ToString() => $"{Name}: {Value}";
}

public sealed record SensorGroup(string Hardware, string Kind, IReadOnlyList<SensorRow> Rows)
{
    public override string ToString() => Hardware;
}

public sealed record DriveRow(DriveReading Drive, DriveVerdict Verdict)
{
    public string Name => Drive.Name;
    public string KindText => Drive.Kind switch { DriveKind.HardDisk => "Hard disk", DriveKind.Ssd => "SSD", DriveKind.Nvme => "NVMe SSD", DriveKind.Usb => "USB drive", _ => "Drive" };
    public string SizeText => Drive.SizeBytes <= 0 ? "" : Drive.SizeBytes >= 900_000_000_000 ? $"{Drive.SizeBytes / 1_000_000_000_000.0:0.#} TB" : $"{Drive.SizeBytes / 1_000_000_000.0:0} GB";
    public string LettersText => Drive.LettersText;
    public string Level => Verdict.Level switch { DriveLevel.Bad => "Bad", DriveLevel.Watch => "Watch", _ => "Good" };
    public string Summary => Verdict.Summary;
    public string Notes => string.Join(" ", Verdict.Notes);
    public bool HasNotes => Verdict.Notes.Count > 0;
    private const string NotReported = "Not reported";

    public string Temperature => Drive.TempC is { } t ? $"{t} °C" : NotReported;
    public string TemperatureTip => (Drive.TempLimitC is { } m ? $"The drive's own warning limit is {m} °C. " : "")
        + (Drive.Kind == DriveKind.HardDisk ? "Spinning disks are happiest below 45 °C; over 55 °C for long shortens their life." : "SSDs are fine up to about 70 °C; above that they slow themselves down to cool off.");

    public string Wear => Drive.Kind is DriveKind.HardDisk ? "Doesn't apply" : Drive.WearPercent is { } w ? $"{w}% used" : NotReported;
    public string WearTip => Drive.Kind is DriveKind.HardDisk
        ? "Spinning disks don't wear out from writing the way SSDs do; watch bad sectors and age instead."
        : "How much of its rated write endurance the SSD has used. 100% is the maker's warranty figure, not a cliff: most drives keep working well past it, but it's time to plan a replacement.";

    public string Hours => Drive.PowerOnHours is { } h ? $"{h:N0} h ({h / 24.0 / 365:0.0} years)" : NotReported;
    public string Errors => Drive.MediaErrors is { } media ? $"{media:N0}"
        : Drive.ReadErrorsUncorrected is null && Drive.WriteErrorsUncorrected is null ? NotReported
        : $"{(Drive.ReadErrorsUncorrected ?? 0) + (Drive.WriteErrorsUncorrected ?? 0)} uncorrected";
    public string ErrorsTip => "Errors the drive couldn't fix on its own. Any number above 0 is worth a backup; a growing number means the drive is failing.";

    // Drive makers rate endurance in decimal terabytes (TBW), so this uses 1 TB = 10^12 bytes, unlike file sizes.
    public string Written => Drive.DataWrittenBytes is { } b ? (b >= 1_000_000_000_000 ? $"{b / 1e12:0.0} TB" : $"{b / 1e9:0} GB") : NotReported;
    public string UnsafeShutdowns => Drive.UnsafeShutdowns is { } u ? $"{u:N0}" : NotReported;
    public string UnsafeTip => "Times the power went away without Windows shutting down first: power cuts, a held power button, or a freeze you had to restart out of. Each one risks losing data that was being written.";
    public string PowerCycles => Drive.PowerCycles is { } p ? $"{p:N0}" : NotReported;
    public string Spare => Drive.AvailableSparePercent is { } s ? $"{s}%" + (Drive.AvailableSpareThreshold is { } t and > 0 ? $" (limit {t}%)" : "") : NotReported;
    public string BadSectors => Drive.ReallocatedSectors is null && Drive.PendingSectors is null ? NotReported
        : $"{Drive.ReallocatedSectors ?? 0} replaced, {Drive.PendingSectors ?? 0} waiting";
    public bool IsNvme => Drive.Kind == DriveKind.Nvme;
    public bool HasSectorData => Drive.ReallocatedSectors is not null || Drive.PendingSectors is not null;
    public bool HasMore => Drive.DataWrittenBytes is not null || Drive.UnsafeShutdowns is not null || Drive.PowerCycles is not null || HasSectorData;
    public string Source => Drive.CountersFrom is { } s ? $"Read from {s}."
        : Drive.HasCounters ? "Read from Windows' storage counters (this drive doesn't share more)."
        : Drive.Kind switch
        {
            DriveKind.Usb => "This drive doesn't report its temperature, wear or errors: most USB sticks and enclosures don't.",
            DriveKind.Nvme => "This drive didn't share its health log.",
            _ => "Windows only gives this drive's temperature, wear and errors to administrators: Read temperature, wear and errors asks once. Some drives don't share them at all.",
        };
    public bool HasCounters => Drive.HasCounters;
    public override string ToString() => $"{Name} {LettersText}: {Summary}";
}

public sealed partial class BlueScreenRow(BlueScreen screen, string? analysisText, string? blamed) : ObservableObject
{
    public BlueScreen Screen { get; } = screen;
    public string When => Screen.At.ToString("ddd d MMM yyyy, h:mm tt");
    public string Ago => Converters.Format.Ago(Screen.At);
    public string Name => Screen.Name;
    public string Meaning => char.ToUpperInvariant(Screen.Summary[0]) + Screen.Summary[1..] + ".";
    public string Code => $"0x{Screen.Code:X}";
    public string? DumpName => Screen.DumpPath is { } p ? System.IO.Path.GetFileName(p) : null;
    public bool CanAnalyze => Screen.DumpPath is { } p && p.Contains("Minidump", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(p);
    [ObservableProperty] private string? _analysis = analysisText;
    [ObservableProperty] private string? _blamed = blamed;
    [ObservableProperty] private bool _isBusy;
    public override string ToString() => $"{Name} on {When}";
}

public sealed record AppCrashRow(string App, string Counts, string Last, string? Module, string? Reason)
{
    public override string ToString() => $"{App}: {Counts}";
}

public sealed record SleepRow(string When, string State, string Why, string Slept, string Resume, string? Detail, bool IsSlow)
{
    public override string ToString() => $"{When}: {State}, resume {Resume}";
}

public sealed record StallRow(string When, string Length)
{
    public override string ToString() => $"{When}: froze for {Length}";
}

public sealed record BootRow(string When, string Total, string Detail, bool IsSlow)
{
    public override string ToString() => $"{When}: start-up {Total}";
}

public sealed record SlowItemRow(string Kind, string Name, string Cost)
{
    public override string ToString() => $"{Kind} {Name}: {Cost}";
}

public sealed record DriverRow(string Device, string Maker, string Version, string Date, bool Signed, string Class, string Inf)
{
    public string SignedText => Signed ? "Signed" : "Unsigned";
    public override string ToString() => $"{Device} {Version}";
}

public sealed partial class DriverPackageRow(DriverPackage package) : ObservableObject
{
    public DriverPackage Package { get; } = package;
    public string Name => $"{Package.Provider} {Package.OriginalName}";
    public string Published => Package.PublishedName;
    public string Version => Package.Version;
    public string Date => Package.Date?.ToString("d MMM yyyy") ?? "";
    public string ClassName => Package.ClassName;
    [ObservableProperty] private bool _isSelected;
    public override string ToString() => $"{Name} {Version}";
}

/// <summary>An app in the "what's using my PC" list: all its processes added together.</summary>
public sealed record AppUseRow(AppUse App, string Cpu, string Gpu, string Ram, string Disk)
{
    public string Name => App.Name;
    public string Instances => App.Instances > 1 ? $"×{App.Instances}" : "";
    public string Role => App.RoleLabel;
    public bool IsSalad => App.Role == ProcessRole.Salad;
    public bool CanEnd => App.CanEnd;
    public bool CanOpenFolder => App.Path is not null;
    public string? Path => App.Path;
    public string EndToolTip => App.Role switch
    {
        ProcessRole.Salad => "Salad is earning on this PC: it's never ended from here",
        ProcessRole.Windows => "Part of Windows: ending it could crash the PC",
        ProcessRole.Self => "That's this app",
        ProcessRole.Protected => "Windows doesn't let C.A.R.S. manage this one",
        _ => "Close it (unsaved work in it is lost)",
    };
    public override string ToString() => $"{Name}: CPU {Cpu}, GPU {Gpu}, memory {Ram}";
}

public sealed partial class PeerRow(TailscalePeer peer, Func<TailscalePeer, int, Task> probe) : ObservableObject
{
    public TailscalePeer Peer { get; } = peer;
    public string Name => Peer.Name;
    public string Os => Peer.Os;
    public string Path => Peer.PathText;
    public string Address => Peer.Address ?? "";
    public bool Online => Peer.Online;
    public string Seen => Peer.Online ? "now" : Peer.LastSeen is { } s ? Converters.Format.Ago(s) : "never";
    public string Flags => string.Join(", ", new[] { Peer.UsingExitNode ? "your exit node" : null, Peer.OffersExitNode ? "offers exit node" : null }.Where(x => x is not null));
    public string FlagsText => Flags.Length == 0 ? "" : " · " + Flags;
    [ObservableProperty] private string? _probeResult;
    public Task ProbeAsync(int port) => probe(Peer, port);
    public override string ToString() => $"{Name}, {Path}";
}

public sealed record SortChoice(string Label, ActivitySort Sort)
{
    public override string ToString() => Label;
}

public sealed record AdapterRow(string Name, string Description, string State, string Speed, bool CarriesTraffic, bool IsVpn);

public sealed class HealthCollections
{
    public ObservableCollection<FindingRow> Findings { get; } = [];
    public ObservableCollection<InfoRow> Specs { get; } = [];
}
