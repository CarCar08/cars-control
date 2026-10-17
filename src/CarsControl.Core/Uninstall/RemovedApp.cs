using System.Text.Json;
using CarsControl.Core.Models;

namespace CarsControl.Core.Uninstall;

/// <summary>Rebuilds an uninstalled app from its History row so its leftovers can be looked for later
/// (History › Find leftovers). Uninstalls since round 3 store the whole entry; older rows only have the
/// key ("arp:LocalMachine64:Steam App 4001890", "msix:{full name}") and the title, which is enough for
/// the name, Steam and LocalLow checks.</summary>
public static class RemovedApp
{
    public static string ToData(AppEntry app) => JsonSerializer.Serialize(new Payload(app));

    public static AppEntry FromHistory(string targetKey, string title, string? data)
    {
        if (data is { Length: > 0 })
        {
            try
            {
                if (JsonSerializer.Deserialize<Payload>(data)?.App is { } saved) return saved;
            }
            catch (JsonException)
            {
                // Not an uninstall payload; fall back to the key.
            }
        }

        var parts = targetKey.Split(':', 3);
        if (parts is ["arp", var hiveText, var keyName] && Enum.TryParse<ArpHive>(hiveText, out var hive))
        {
            var loc = new ArpLocation(hive, keyName);
            return new AppEntry { Key = targetKey, Name = title, Source = AppSource.Arp, Arp = loc, Scope = loc.Scope };
        }
        if (parts is ["msix", ..])
            return new AppEntry { Key = targetKey, Name = title, Source = AppSource.Msix, MsixFullName = targetKey["msix:".Length..], Scope = InstallScope.User };
        return new AppEntry { Key = targetKey, Name = title };
    }

    private sealed record Payload(AppEntry App);
}
