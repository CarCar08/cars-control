using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Tweaks;
using Microsoft.Win32;

namespace CarsControl.App.Services;

/// <summary>
/// Applies, undoes and resets catalog switches for the Privacy and Optimization pages. Your-account switches are changed here, as
/// you, with the value from before the first change kept in cars.db; admin ones go through the helper, which keeps its own copy.
/// One shared instance, because both pages' snapshots live under the same key.
/// </summary>
public sealed class TweakSwitcher(HelperClient helper, CarsDb db)
{
    private const string SnapshotKey = "tweak_snapshots_v1";
    private readonly string? _sid = WindowsIdentity.GetCurrent().User?.Value;
    private Dictionary<string, List<SavedValue>>? _snapshots;

    private Dictionary<string, List<SavedValue>> Snapshots => _snapshots ??= Load();

    public TweakState Read(Tweak t) => TweakEngine.Read(t, t.Scope == TweakScope.Machine ? Registry.LocalMachine : Registry.CurrentUser);

    /// <summary>Whether switching it off puts back a saved earlier value (otherwise it goes to Windows' default).</summary>
    public bool CanUndo(Tweak t) => t.Scope == TweakScope.User ? Snapshots.ContainsKey(t.Id) : TweakSnapshots.HelperHasUndo(t, _sid);

    /// <summary>"apply", "undo" (back to exactly what was there before) or "default" (how Windows ships).</summary>
    public async Task<(bool Ok, string Message)> SetAsync(Tweak t, string action)
    {
        if (t.Scope == TweakScope.User)
        {
            try
            {
                var root = Registry.CurrentUser;
                switch (action)
                {
                    case "apply":
                        if (!Snapshots.ContainsKey(t.Id)) Snapshots[t.Id] = TweakEngine.Snapshot(t, root).ToList();
                        TweakEngine.Apply(t, root);
                        break;
                    case "undo" when Snapshots.TryGetValue(t.Id, out var saved):
                        TweakEngine.Restore(t, saved, root);
                        Snapshots.Remove(t.Id);
                        break;
                    default:
                        TweakEngine.SetDefaults(t, root);
                        Snapshots.Remove(t.Id);
                        break;
                }
                Save();
                AfterChange(t);
                return (true, "Done.");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                return (false, $"Windows refused the change: {ex.Message}");
            }
        }

        try
        {
            var r = await helper.SendAsync(new TweakOp(t.Id, action), dryRun: HelperClient.DevUnelevated);
            return r.Ok ? (true, r.Message ?? "Done.") : (false, r.Message ?? r.Status.ToString());
        }
        catch (HelperCancelledException)
        {
            return (false, "Admin approval was cancelled, so nothing changed.");
        }
        catch (HelperUnavailableException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Shows a row's live state: on, partly, whether Undo has something saved, any caution for this PC.</summary>
    public void ReadInto(ViewModels.TweakRowViewModel row)
    {
        var state = Read(row.Tweak);
        row.Syncing = true;
        row.IsOn = state == TweakState.On;
        row.Syncing = false;
        row.IsPartly = state == TweakState.Partly;
        row.CanUndo = CanUndo(row.Tweak);
        row.Caution = row.Tweak.Caution?.Invoke();
        if (row.IsPartly && row.Status is null) row.Status = "Partly on (something else changed some of its settings)";
    }

    /// <summary>One switch flipped (or reset) from its row: busy while it runs, the outcome in the row's status, recorded in History.</summary>
    public async Task RunRowAsync(ViewModels.TweakRowViewModel row, string action)
    {
        if (row.IsBusy) return;
        row.IsBusy = true;
        row.Status = null;
        try
        {
            var r = await SetAsync(row.Tweak, action);
            if (!r.Ok)
            {
                row.Status = r.Message;
                return;
            }
            Record(row.Tweak, action switch { "apply" => "Turned on", "undo" => "Undone", _ => "Set to the Windows default" });
            row.Status = action == "default" ? "Back to the Windows default."
                : row.Tweak.TakesEffect is { } when ? $"Done. Takes effect {when}." : null;
        }
        finally
        {
            row.IsBusy = false;
            ReadInto(row);
        }
    }

    public void Record(Tweak t, string what) =>
        db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, t.IsOptimization ? "optimize" : "privacy", $"tweak:{t.Id}", t.Title,
            t.IsOptimization ? "Optimization" : "Privacy", null, null, t.NeedsAdmin, "Succeeded", 0, what, TweakEngine.Describe(t)));

    /// <summary>Tells Explorer, the taskbar and running apps, so most switches apply without signing out.</summary>
    private static void AfterChange(Tweak t)
    {
        if (t.Group is TweakGroup.Taskbar or TweakGroup.AI or TweakGroup.Ads or TweakGroup.Visuals) BroadcastSettingChange();
        if (t.Id == "mouse-acceleration-off") MouseSettings.ApplyFromRegistry();
    }

    private Dictionary<string, List<SavedValue>> Load()
    {
        try
        {
            if (db.GetSetting(SnapshotKey) is { Length: > 0 } json)
                return JsonSerializer.Deserialize<Dictionary<string, List<SavedValue>>>(json) ?? new();
        }
        catch (JsonException)
        {
            // unreadable: start over
        }
        return new();
    }

    private void Save() => db.SetSetting(SnapshotKey, JsonSerializer.Serialize(Snapshots));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    private static void BroadcastSettingChange()
    {
        const uint WmSettingChange = 0x001A, SmtoAbortIfHung = 0x0002;
        _ = Task.Run(() => SendMessageTimeout(new IntPtr(0xFFFF), WmSettingChange, IntPtr.Zero, "TraySettings", SmtoAbortIfHung, 1000, out _));
    }
}

/// <summary>"Enhance pointer precision" takes effect at sign-in from the registry; this applies the saved values right away.</summary>
internal static class MouseSettings
{
    public static void ApplyFromRegistry()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
        int Get(string name, int fallback) => int.TryParse(k?.GetValue(name) as string, out var n) ? n : fallback;
        var values = new[] { Get("MouseThreshold1", 6), Get("MouseThreshold2", 10), Get("MouseSpeed", 1) };
        SystemParametersInfo(SpiSetMouse, 0, values, SpifSendChange);
    }

    private const uint SpiSetMouse = 0x0004, SpifSendChange = 0x2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] values, uint winIni);
}
