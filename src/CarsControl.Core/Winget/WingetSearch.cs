using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Management.Deployment;

namespace CarsControl.Core.Winget;

/// <summary>A package from winget's catalogs (winget community repository or the Microsoft Store).</summary>
public sealed record WingetResult(string Id, string Name, string Publisher, string? Version, string Source, string? InstalledVersion)
{
    public bool IsInstalled => InstalledVersion is not null;
    public bool IsStore => Source.Equals("msstore", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>Searches winget's catalogs through the winget COM API (as you, no admin).</summary>
public static class WingetSearch
{
    public static Task<IReadOnlyList<WingetResult>> SearchAsync(string query, int max = 40, bool includeStore = true, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<WingetResult>>(() => Search(query, max, includeStore, ct), ct);

    private static IReadOnlyList<WingetResult> Search(string query, int max, bool includeStore, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length < 2) return [];

        PackageManager pm;
        try
        {
            pm = WingetCom.CreatePackageManager();
        }
        catch (Exception ex)
        {
            throw new WingetUnavailableException("The winget COM server could not be started. Is App Installer installed and is this process unelevated?", ex);
        }

        var results = new List<WingetResult>();
        var catalogs = new List<PredefinedPackageCatalog> { PredefinedPackageCatalog.OpenWindowsCatalog };
        if (includeStore) catalogs.Add(PredefinedPackageCatalog.MicrosoftStore);
        foreach (var predefined in catalogs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.AddRange(SearchCatalog(pm, predefined, query, max, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && predefined == PredefinedPackageCatalog.MicrosoftStore)
            {
                // The Store source may be missing or offline: the community results are still worth showing.
            }
        }
        return Rank(results, query).Take(max).ToList();
    }

    private static IEnumerable<WingetResult> SearchCatalog(PackageManager pm, PredefinedPackageCatalog predefined, string query, int max, CancellationToken ct)
    {
        var remote = pm.GetPredefinedPackageCatalog(predefined);
        // A composite with the installed packages tells which results you already have.
        var opts = WingetCom.CreateCompositeOptions();
        opts.Catalogs.Add(remote);
        opts.CompositeSearchBehavior = CompositeSearchBehavior.RemotePackagesFromAllCatalogs;
        var connect = pm.CreateCompositePackageCatalog(opts).Connect();
        if (connect.Status != ConnectResultStatus.Ok) throw new WingetUnavailableException($"Could not open the winget source: {connect.Status}");

        var options = WingetCom.CreateFindPackagesOptions();
        var filter = WingetCom.CreatePackageMatchFilter();
        filter.Field = PackageMatchField.CatalogDefault;
        filter.Option = PackageFieldMatchOption.ContainsCaseInsensitive;
        filter.Value = query;
        options.Selectors.Add(filter);
        // Ask for more than will be shown: ranking drops translations of the same app (Firefox has 100 locales).
        options.ResultLimit = (uint)Math.Min(max * 4, 200);

        var found = connect.PackageCatalog.FindPackages(options);
        if (found.Status != FindPackagesResultStatus.Ok) throw new WingetUnavailableException($"winget search failed: {found.Status}");
        var matches = found.Matches;
        var count = Math.Min(matches.Count, max * 4);
        var list = new List<WingetResult>(count);
        var source = predefined == PredefinedPackageCatalog.MicrosoftStore ? "msstore" : "winget";
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var pkg = matches[i].CatalogPackage;
                var latest = pkg.DefaultInstallVersion;
                list.Add(new WingetResult(pkg.Id, pkg.Name, latest?.Publisher ?? "", latest?.Version, source, pkg.InstalledVersion?.Version));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One malformed manifest must not hide the rest.
            }
        }
        return list;
    }

    /// <summary>"Mozilla Firefox (de)" with id "Mozilla.Firefox.de": a translation of an app that also has its main entry.</summary>
    public static bool IsTranslation(WingetResult r) => WingetSearchRules.LocaleName().IsMatch(r.Name) && WingetSearchRules.LocaleId().IsMatch(r.Id);

    /// <summary>Exact name or id first, then names that start with the query, then the rest; the community repository before the Store.</summary>
    public static IEnumerable<WingetResult> Rank(IEnumerable<WingetResult> results, string query) =>
        results.GroupBy(r => (r.Id, r.Source)).Select(g => g.First())
            .OrderBy(r => IsTranslation(r) ? 1 : 0)
            .ThenBy(r => r.Name.Equals(query, StringComparison.OrdinalIgnoreCase) || r.Id.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0
                : r.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1
                : r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : 3)
            .ThenBy(r => r.IsStore ? 1 : 0)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
}

public static partial class WingetSearchRules
{
    [System.Text.RegularExpressions.GeneratedRegex(@"\.[a-z]{2,3}(-[A-Za-z]{2,9})?$")]
    internal static partial System.Text.RegularExpressions.Regex LocaleId();

    [System.Text.RegularExpressions.GeneratedRegex(@"\((?!en-US)[a-z]{2,3}(-[A-Za-z]{2,9})?\)$")]
    internal static partial System.Text.RegularExpressions.Regex LocaleName();
}

/// <summary>One line of a saved app list.</summary>
public sealed record AppListEntry(string Id, string Name, string Source = "winget");

/// <summary>
/// A list of apps you want on a PC, saved as a small JSON file: "what to install on a new PC". It also reads the
/// file <c>winget export</c> writes, so a list from either tool works. Every id is checked before it can reach
/// a command line.
/// </summary>
public static class AppList
{
    public const string Format = "cars-control-app-list";
    public const int MaxApps = 500;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record Document(string Format, int Version, DateTime Created, List<AppListEntry> Apps);

    public static string Serialize(IEnumerable<AppListEntry> apps) =>
        JsonSerializer.Serialize(new Document(Format, 1, DateTime.UtcNow, Clean(apps).ToList()), Options);

    /// <summary>Entries from a C.A.R.S. list or a winget export; invalid ones are dropped and counted in <paramref name="skipped"/>.</summary>
    public static IReadOnlyList<AppListEntry> Parse(string json, out int skipped)
    {
        skipped = 0;
        var raw = new List<AppListEntry>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];

            if (root.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in apps.EnumerateArray())
                    raw.Add(new AppListEntry(Str(a, "id") ?? "", Str(a, "name") ?? "", Str(a, "source") ?? "winget"));
            }
            else if (root.TryGetProperty("Sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            {
                // winget export: { "Sources": [ { "Packages": [ { "PackageIdentifier": "..." } ], "SourceDetails": { "Name": "winget" } } ] }
                foreach (var s in sources.EnumerateArray())
                {
                    var name = s.TryGetProperty("SourceDetails", out var d) ? Str(d, "Name") ?? "winget" : "winget";
                    if (!s.TryGetProperty("Packages", out var pk) || pk.ValueKind != JsonValueKind.Array) continue;
                    foreach (var p in pk.EnumerateArray())
                        raw.Add(new AppListEntry(Str(p, "PackageIdentifier") ?? "", Str(p, "PackageIdentifier") ?? "", name));
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        var clean = Clean(raw).ToList();
        skipped = raw.Count - clean.Count;
        return clean;
    }

    public static IEnumerable<AppListEntry> Clean(IEnumerable<AppListEntry> apps) =>
        apps.Where(a => WingetCli.IsValidId(a.Id) && a.Source.ToLowerInvariant() is "winget" or "msstore")
            .Select(a => new AppListEntry(a.Id, Tidy(a.Name, a.Id), a.Source.ToLowerInvariant()))
            .DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Take(MaxApps);

    private static string Tidy(string name, string id)
    {
        var n = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return n.Length == 0 ? id : n.Length > 120 ? n[..120] : n;
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
