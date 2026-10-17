using System.Text.Json;
using Microsoft.Win32;

namespace CarsControl.Core.Tweaks;

public enum TweakState { On, Off, Partly }

/// <summary>A value as it was before a change: Exists = false means it didn't exist.</summary>
public sealed record SavedValue(string Key, string Name, bool Exists, RegistryValueKind Kind = RegistryValueKind.DWord, string? Data = null);

/// <summary>
/// Reads and writes a tweak's values under a hive root (HKCU, HKLM or HKEY_USERS\{SID}; tests pass a
/// scratch key). Before changing anything it snapshots what was there, so Undo puts back exactly that,
/// including deleting values that didn't exist.
/// </summary>
public static class TweakEngine
{
    public static TweakState Read(Tweak t, RegistryKey root)
    {
        var matches = t.Values.Count(v => IsRecommended(root, v));
        return matches == t.Values.Count ? TweakState.On : matches == 0 ? TweakState.Off : TweakState.Partly;
    }

    private static bool IsRecommended(RegistryKey root, TweakValue v)
    {
        using var k = root.OpenSubKey(v.Key);
        var current = k?.GetValue(v.Name);
        return current is not null && Same(current, v.Recommended);
    }

    private static bool Same(object current, object wanted) => wanted switch
    {
        int n => current is int c && c == n,
        string s => current is string c && string.Equals(c, s, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    public static IReadOnlyList<SavedValue> Snapshot(Tweak t, RegistryKey root) => t.Values.Select(v =>
    {
        using var k = root.OpenSubKey(v.Key);
        var value = k?.GetValue(v.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (k is null || value is null) return new SavedValue(v.Key, v.Name, false);
        var kind = k.GetValueKind(v.Name);
        return new SavedValue(v.Key, v.Name, true, kind, value switch
        {
            int n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string s => s,
            _ => null, // binary / multi-string: not used by any tweak; restored as "didn't exist" would be wrong
        });
    }).ToList();

    public static void Apply(Tweak t, RegistryKey root)
    {
        foreach (var v in t.Values)
        {
            using var k = root.CreateSubKey(v.Key, writable: true);
            k.SetValue(v.Name, v.Recommended, v.Kind);
        }
    }

    /// <summary>Puts back what <see cref="Snapshot"/> saw. Only the tweak's own values are touched.</summary>
    public static void Restore(Tweak t, IReadOnlyList<SavedValue> saved, RegistryKey root)
    {
        foreach (var s in saved)
        {
            if (!t.Values.Any(v => v.Key == s.Key && v.Name == s.Name)) continue; // never a value outside the tweak
            if (!s.Exists)
            {
                using var existing = root.OpenSubKey(s.Key, writable: true);
                existing?.DeleteValue(s.Name, throwOnMissingValue: false);
                continue;
            }
            using var k = root.CreateSubKey(s.Key, writable: true);
            switch (s.Kind)
            {
                case RegistryValueKind.DWord when int.TryParse(s.Data, out var n):
                    k.SetValue(s.Name, n, RegistryValueKind.DWord);
                    break;
                case RegistryValueKind.QWord when long.TryParse(s.Data, out var l):
                    k.SetValue(s.Name, l, RegistryValueKind.QWord);
                    break;
                case RegistryValueKind.String or RegistryValueKind.ExpandString when s.Data is not null:
                    k.SetValue(s.Name, s.Data, s.Kind);
                    break;
            }
        }
    }

    /// <summary>What Windows has out of the box: policies removed, settings back to their defaults.</summary>
    public static void SetDefaults(Tweak t, RegistryKey root)
    {
        foreach (var v in t.Values)
        {
            if (v.Default is null)
            {
                using var existing = root.OpenSubKey(v.Key, writable: true);
                existing?.DeleteValue(v.Name, throwOnMissingValue: false);
            }
            else
            {
                using var k = root.CreateSubKey(v.Key, writable: true);
                k.SetValue(v.Name, v.Default, v.Kind);
            }
        }
    }

    public static string Serialize(IReadOnlyList<SavedValue> saved) => JsonSerializer.Serialize(saved);

    public static IReadOnlyList<SavedValue>? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<SavedValue>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"HKCU\Software\…\Name = 0" lines for a tooltip.</summary>
    public static string Describe(Tweak t)
    {
        var hive = t.Scope == TweakScope.Machine ? "HKLM" : "HKCU";
        return string.Join(Environment.NewLine, t.Values.Select(v => $@"{hive}\{v.Key}\{v.Name} = {v.Recommended}"));
    }
}
