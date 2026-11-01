using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MonitorAndControlWatchdog;

internal static class ScreenTimeWindows
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Session { public int Id; public IntPtr Name; public int State; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo
    {
        public string Name; public string Password; public uint PasswordAge; public uint Privilege;
        public string Home; public string Comment; public uint Flags; public string Script;
    }
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int info, out IntPtr buffer, out int size);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr server, int sessionId, bool wait);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WTSSendMessageW(IntPtr server, int session, string title, int titleLength,
        string message, int messageLength, int style, int timeout, out int response, bool wait);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserEnum(string? server, int level, int filter, out IntPtr buffer,
        int maxLength, out int read, out int total, ref int resume);
    [DllImport("Netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buffer);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetLocalGroups(string? server, string name, int level, int flags,
        out IntPtr buffer, int maxLength, out int read, out int total);

    internal sealed record Account(string Name, string Sid, bool Eligible = true, string? ExclusionReason = null)
    { public override string ToString() => Name; }
    internal sealed record UserSession(int SessionId, string Sid, bool Unlocked);

    internal static List<Account> Accounts(bool includeExcluded = false)
    {
        var result = new List<Account>();
        int resume = 0;
        var error = NetUserEnum(null, 1, 2, out var buffer, -1, out var count, out _, ref resume);
        if (error != 0) throw new Win32Exception(error);
        try
        {
            for (int i = 0; i < count; i++)
            {
                var user = Marshal.PtrToStructure<UserInfo>(buffer + i * Marshal.SizeOf<UserInfo>());
                var excluded = (user.Flags & 2) != 0 ? "Account disabled" : (user.Privilege == 2 || IsAdministrator(user.Name)) ? "Administrator" : null;
                if (!includeExcluded && excluded != null) continue;
                var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, user.Name).Translate(typeof(SecurityIdentifier));
                result.Add(new Account(user.Name, sid.Value, excluded == null, excluded));
            }
        }
        finally { NetApiBufferFree(buffer); }
        return result;
    }

    private static bool IsAdministrator(string name)
    {
        var error = NetUserGetLocalGroups(null, name, 0, 1, out var buffer, -1, out var count, out _);
        if (error != 0) throw new Win32Exception(error);
        try
        {
            for (int i = 0; i < count; i++)
            {
                var group = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size))!;
                var sid = (SecurityIdentifier)new NTAccount(group).Translate(typeof(SecurityIdentifier));
                if (sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) return true;
            }
        }
        finally { NetApiBufferFree(buffer); }
        return false;
    }

    internal static List<UserSession> Sessions()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new List<UserSession>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<Session>(buffer + i * Marshal.SizeOf<Session>());
                if (session.Id == 0 || session.State != 0 || !WTSQueryUserToken((uint)session.Id, out var token)) continue;
                try
                {
                    using var identity = new WindowsIdentity(token);
                    if (identity.User == null || identity.Groups == null
                        || identity.Groups.Any(g => g.Value == "S-1-5-32-544")) continue;
                    // WTSINFOEX x64: Level at 0, aligned LEVEL1 at 8, SessionFlags at 16.
                    bool unlocked = false;
                    if (WTSQuerySessionInformationW(IntPtr.Zero, session.Id, 25, out var info, out var size))
                    {
                        try { unlocked = size >= 20 && Marshal.ReadInt32(info) == 1 && Marshal.ReadInt32(info, 16) == 1; }
                        finally { WTSFreeMemory(info); }
                    }
                    result.Add(new UserSession(session.Id, identity.User.Value, unlocked));
                }
                finally { CloseHandle(token); }
            }
        }
        finally { WTSFreeMemory(buffer); }
        return result;
    }
    internal static void Warn(int session, string message)
    {
        const string title = "Monitor & Control - PC time";
        WTSSendMessageW(IntPtr.Zero, session, title, title.Length * 2, message, message.Length * 2,
            0x40, 30, out _, false);
    }
    internal static void Logoff(int session, string expectedSid)
    {
        if (!Sessions().Any(s => s.SessionId == session && s.Sid == expectedSid)) return;
        if (!WTSLogoffSession(IntPtr.Zero, session, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
