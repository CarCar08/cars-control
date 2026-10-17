using CarsControl.Contracts;
using CarsControl.Core.Tweaks;
using Microsoft.Win32;

namespace CarsControl.Core.Elevation;

/// <summary>
/// Privacy switches that need admin, inside the helper. Only ids from <see cref="TweakCatalog"/> with a
/// Machine or UserPolicy scope are accepted, and only that tweak's own values are written. Your policy
/// values go into your own hive (HKEY_USERS\{your SID}); the helper's HKCU would be the admin account's.
/// The value before the first change is kept in ProgramData\CarsControl\tweaks (admins write, users read),
/// so Undo restores exactly that.
/// </summary>
internal static class TweakOps
{
    public static OpResult Execute(TweakOp op, bool dryRun, HelperLog log, string? parentSid)
    {
        if (TweakCatalog.Find(op.Id) is not { } tweak) return OpResult.Reject($"Unknown privacy switch '{op.Id}'.");
        if (tweak.Scope == TweakScope.User) return OpResult.Reject("Your-account switches are changed by the app itself, not as admin.");
        if (op.Action is not ("apply" or "undo" or "default")) return OpResult.Reject($"Unknown action '{op.Action}'.");
        if (tweak.Scope == TweakScope.UserPolicy && (parentSid is null || !parentSid.StartsWith("S-1-5-21-", StringComparison.Ordinal)))
            return OpResult.Reject("Couldn't tell whose policies to change.");
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, $"Would {op.Action} \"{tweak.Title}\":{Environment.NewLine}{TweakEngine.Describe(tweak)}");

        using var root = tweak.Scope == TweakScope.Machine
            ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            : Registry.Users.OpenSubKey(parentSid!, writable: true);
        if (root is null) return new OpResult(OpStatus.Failed, -1, "Your registry hive isn't loaded.");

        var file = SnapshotFile(tweak, parentSid, ensure: true);
        switch (op.Action)
        {
            case "apply":
                // Only the first change is remembered: Undo goes back to how it was before C.A.R.S. touched it.
                if (!File.Exists(file)) File.WriteAllText(file, TweakEngine.Serialize(TweakEngine.Snapshot(tweak, root)));
                TweakEngine.Apply(tweak, root);
                break;
            case "undo" when TweakEngine.Deserialize(File.Exists(file) ? File.ReadAllText(file) : null) is { } saved:
                TweakEngine.Restore(tweak, saved, root);
                File.Delete(file);
                break;
            default: // "default", or "undo" with nothing saved
                TweakEngine.SetDefaults(tweak, root);
                if (File.Exists(file)) File.Delete(file);
                break;
        }
        log.Write($"tweak {tweak.Id} {op.Action} ({tweak.Scope})");
        return OpResult.Success($"{tweak.Title}: done.", TweakEngine.Read(tweak, root).ToString());
    }

    /// <summary>ProgramData\CarsControl\tweaks\{id}[-{sid}].json. Readable by the app (to show Undo).</summary>
    public static string SnapshotFile(Tweak tweak, string? sid, bool ensure = false)
    {
        var dir = ensure ? SecureDataDir.Ensure("tweaks") : Path.Combine(SecureDataDir.Root, "tweaks");
        var name = tweak.Scope == TweakScope.UserPolicy ? $"{tweak.Id}-{sid}.json" : $"{tweak.Id}.json";
        return Path.Combine(dir, name);
    }
}

/// <summary>For the app: whether the helper holds an undo snapshot for a tweak.</summary>
public static class TweakSnapshots
{
    public static bool HelperHasUndo(Tweak tweak, string? userSid)
    {
        try
        {
            return File.Exists(TweakOps.SnapshotFile(tweak, userSid));
        }
        catch (IOException)
        {
            return false;
        }
    }
}
