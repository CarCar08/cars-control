using System.Management;

namespace CarsControl.Core.Health;

/// <summary>Detached values only: callers never retain WMI objects or their native handles between refreshes.</summary>
internal sealed record WmiRow(IReadOnlyDictionary<string, object?> Values)
{
    public object? this[string name] => Values.GetValueOrDefault(name);
}

internal static class WmiQuery
{
    public static List<WmiRow> Read(string scope, string query)
    {
        using var searcher = new ManagementObjectSearcher(scope, query);
        using var results = searcher.Get();
        var rows = new List<WmiRow>();
        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (PropertyData property in item.Properties)
                    if (property.Value is not ManagementBaseObject) values[property.Name] = property.Value;
                rows.Add(new WmiRow(values));
            }
        }
        return rows;
    }
}
