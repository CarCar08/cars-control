using CarsControl.Core.Models;
using CarsControl.Core.Storage;
using CarsControl.Core.Winget;

namespace CarsControl.Core.Inventory;

public sealed record InventorySnapshot(
    IReadOnlyList<AppEntry> Apps,
    IReadOnlyList<WingetInstalled> Winget,
    string? WingetError,
    DateTime TakenAt);

/// <summary>Builds the unified app list: ARP (3 registry views) + MSIX, correlated with winget,
/// then classified by update channel and protection.</summary>
public sealed class InventoryService
{
    public ProtectedApps Protected { get; set; } = new();

    public async Task<InventorySnapshot> LoadAsync(CancellationToken ct = default)
    {
        var arpTask = Task.Run(ArpProvider.ReadAll, ct);
        var msixTask = Task.Run(MsixProvider.ReadAll, ct);

        IReadOnlyList<WingetInstalled> winget = [];
        string? wingetError = null;
        try
        {
            winget = await WingetProvider.GetInstalledAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            wingetError = ex.Message;
        }

        var apps = (await arpTask).Concat(await msixTask).ToList();
        Correlator.Apply(apps, winget);
        foreach (var a in apps)
        {
            a.Channel = ChannelClassifier.Classify(a);
            a.IsProtected = Protected.IsProtected(a);
        }
        apps.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
        return new InventorySnapshot(apps, winget, wingetError, DateTime.Now);
    }

    public static readonly TimeSpan SizeFreshFor = TimeSpan.FromDays(7);

    /// <summary>Copies measured sizes from a previous snapshot onto the same app (same key and version)
    /// when the measurement is still fresh, so only new or changed apps need their folders re-read.</summary>
    public static int CarryOverSizes(IEnumerable<AppEntry> fresh, IEnumerable<AppEntry>? previous)
    {
        if (previous is null) return 0;
        var old = previous.Where(p => p.SizeIsMeasured && p.SizeMeasuredAt is { } t && DateTime.Now - t < SizeFreshFor)
                          .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First());
        var carried = 0;
        foreach (var a in fresh)
        {
            if (!old.TryGetValue(a.Key, out var p) || p.Version != a.Version) continue;
            a.SizeBytes = p.SizeBytes;
            a.SizeIsMeasured = true;
            a.SizeMeasuredAt = p.SizeMeasuredAt;
            carried++;
        }
        return carried;
    }

    /// <summary>Replaces registry EstimatedSize with the real size of each install folder, in the
    /// background, skipping folders measured recently. Two threads: these are mostly huge game folders,
    /// and more parallelism just makes the disks seek.</summary>
    public static Task MeasureSizesAsync(IEnumerable<AppEntry> apps, Action<AppEntry> onMeasured, CancellationToken ct = default)
    {
        var targets = apps
            .Where(a => a.InstallLocation is { } loc && !DirectorySizer.IsTooBroadToMeasure(loc))
            .Where(a => !(a.SizeIsMeasured && a.SizeMeasuredAt is { } t && DateTime.Now - t < SizeFreshFor))
            .ToList();
        return Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, (a, token) =>
        {
            try
            {
                var size = DirectorySizer.Measure(a.InstallLocation!, token);
                if (size > 0)
                {
                    a.SizeBytes = size;
                    a.SizeIsMeasured = true;
                    a.SizeMeasuredAt = DateTime.Now;
                    onMeasured(a);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unreadable folders keep their registry estimate.
            }
            return ValueTask.CompletedTask;
        });
    }
}
