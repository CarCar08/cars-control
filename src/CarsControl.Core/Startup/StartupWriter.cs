using Microsoft.Win32;

namespace CarsControl.Core.Startup;

/// <summary>The part of an item's id after its kind: ("run", "HKCU", "OneDrive"), ("folder", "user", "x.lnk"), ("svc", "", "Name"), ("task", "", "\Folder\Name").</summary>
public sealed record StartupId(string Kind, string Where, string Name)
{
    public static StartupId? Parse(string id)
    {
        var parts = id.Split(':', 3);
        return parts switch
        {
            ["run", var where, var name] when where is "HKCU" or "HKLM" or "HKLM32" && name.Length > 0 => new StartupId("run", where, name),
            ["folder", var where, var name] when where is "user" or "common" && name.Length > 0 => new StartupId("folder", where, name),
            ["svc", var name] when name.Length > 0 => new StartupId("svc", "", name),
            ["task", var rest] when rest.StartsWith('\\') => new StartupId("task", "", rest),
            _ => null,
        };
    }
}

/// <summary>Changes the start-up items that belong to you, as you: no admin involved.</summary>
public static class StartupWriter
{
    public static bool IsPermanentlyProtected(StartupItem item) =>
        Inventory.ProtectedApps.IsAlwaysProtectedName(item.Name)
        || Inventory.ProtectedApps.IsAlwaysProtectedName(item.Publisher)
        || Inventory.ProtectedApps.IsAlwaysProtectedName(item.Command)
        || Inventory.ProtectedApps.IsAlwaysProtectedName(item.Exe);

    /// <summary>Your Run entries and your Startup-folder shortcuts. False when the item isn't one of those.</summary>
    public static bool TrySetYours(StartupItem item, bool enable)
    {
        if (IsPermanentlyProtected(item)) return false;
        if (StartupId.Parse(item.Id) is not { } id) return false;
        string? subKey = id switch
        {
            { Kind: "run", Where: "HKCU" } => StartupApproval.ExplorerKey + @"\Run",
            { Kind: "folder", Where: "user" } => StartupApproval.ExplorerKey + @"\StartupFolder",
            _ => null,
        };
        if (subKey is null) return false;
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(id.Name, enable ? StartupApproval.Enabled() : StartupApproval.Disabled(DateTime.UtcNow), RegistryValueKind.Binary);
        return true;
    }

    /// <summary>Your own scheduled tasks can be switched without admin.</summary>
    public static bool TrySetTask(string taskPath, bool enable)
    {
        if (!TaskRules.IsListable(taskPath) || TaskRules.IsProtected(taskPath)) return false;
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            service.Connect();
            dynamic task = service.GetFolder("\\").GetTask(taskPath);
            if (!TaskRules.MayChangeDefinition((string)task.Xml)) return false;
            task.Enabled = enable;
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false; // not yours to change: the helper does it
        }
    }
}
