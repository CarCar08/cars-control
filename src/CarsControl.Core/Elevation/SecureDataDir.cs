using System.Security.AccessControl;
using System.Security.Principal;

namespace CarsControl.Core.Elevation;

/// <summary>
/// C:\ProgramData\CarsControl holds helper logs and quarantined leftovers. Standard users may create
/// folders under ProgramData, so you could pre-create this tree (or plant a junction in it) and steer
/// the elevated helper's writes somewhere else, e.g. System32. Resetting the ACL alone is not enough:
/// the owner of a folder can always rewrite its ACL. So before the helper writes anything:
///   - junctions/symlinks are removed (the link only, never the target),
///   - any folder not owned by Administrators/SYSTEM is moved aside as "*.untrusted-{time}" untouched,
///   - a fresh folder is created, owned by Administrators, ACL = SYSTEM/Administrators full, Users read.
/// The unelevated side never creates this folder.
/// </summary>
public static class SecureDataDir
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CarsControl");

    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private static readonly object Gate = new();
    private static bool _rootSecured;

    /// <summary>Elevated only. Returns a secured subfolder of ProgramData\CarsControl.</summary>
    public static string Ensure(string subfolder)
    {
        lock (Gate)
        {
            if (!_rootSecured)
            {
                MakeTrusted(Root, applyAcl: true);
                _rootSecured = true;
            }
            var path = Path.Combine(Root, subfolder);
            MakeTrusted(path, applyAcl: false); // inherits the root's protected ACL
            return path;
        }
    }

    /// <summary>
    /// Elevated only. A secured subfolder that, instead of "users read", lets only SYSTEM, Administrators and the user who
    /// launched the helper read it: for things that are personal to that user or show other accounts' data (drive listings,
    /// copied crash dumps).
    /// </summary>
    public static string EnsurePrivate(string subfolder, string? parentSid)
    {
        var dir = Ensure(subfolder);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (parentSid is not null)
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(parentSid), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(sec);
        return dir;
    }

    private static void MakeTrusted(string path, bool applyAcl)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                if (attrs.HasFlag(FileAttributes.Directory)) Directory.Delete(path, recursive: false);
                else File.Delete(path);
            }
            else if (!attrs.HasFlag(FileAttributes.Directory) || !IsTrustedOwner(path))
            {
                var aside = $"{path}.untrusted-{DateTime.Now:yyyyMMdd-HHmmss}";
                if (attrs.HasFlag(FileAttributes.Directory)) Directory.Move(path, aside);
                else File.Move(path, aside);
            }
        }

        var dir = Directory.CreateDirectory(path);
        if (!applyAcl) return;

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var sec = new DirectorySecurity();
        sec.SetOwner(Admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(sec);
    }

    internal static bool IsTrustedOwner(string path)
    {
        var owner = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner is not null && (owner.Equals(Admins) || owner.Equals(SystemSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || owner.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"); // TrustedInstaller
    }
}
