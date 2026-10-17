using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CarsControl.Core.Elevation;

/// <summary>Win32 calls the helper uses to prove who it is talking to.</summary>
internal static class Native
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder exeName, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    public static int? PipeServerPid(SafePipeHandle h) => GetNamedPipeServerProcessId(h, out var pid) ? (int)pid : null;

    public static int? PipeClientPid(SafePipeHandle h) => GetNamedPipeClientProcessId(h, out var pid) ? (int)pid : null;

    public static string? ImagePath(int pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        var size = sb.Capacity;
        return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
    }

    /// <summary>The user a process runs as (e.g. you, when the helper runs as an admin account).</summary>
    public static SecurityIdentifier? ProcessUser(int pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid || !OpenProcessToken(h, TOKEN_QUERY, out var token)) return null;
        using (token)
        {
            using var id = new WindowsIdentity(token.DangerousGetHandle());
            return id.User;
        }
    }

    public static string? ProfilePath(SecurityIdentifier sid)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid.Value}");
        return k?.GetValue("ProfileImagePath") as string is { } p ? Environment.ExpandEnvironmentVariables(p) : null;
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
