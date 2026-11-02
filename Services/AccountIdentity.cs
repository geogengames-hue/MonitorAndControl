using System.Security.Principal;

namespace MonitorAndControl.Services;

/// <summary>Which Windows account this DeviceMon instance is running for (each signed-in account runs its own).</summary>
public static class AccountIdentity
{
    private static string? _sid;

    /// <summary>The account's SID (S-1-5-21-...), or an empty string when it cannot be determined.</summary>
    public static string CurrentSid
    {
        get
        {
            if (_sid != null) return _sid;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                _sid = identity.User?.Value ?? "";
            }
            catch { _sid = ""; }
            return _sid;
        }
    }

    private static bool? _admin;

    /// <summary>
    /// True for an administrator account (the parent's), also while UAC runs it with a filtered token
    /// (the Administrators group is then present as deny-only, which still shows up as a claim).
    /// </summary>
    public static bool IsAdministrator
    {
        get
        {
            if (_admin is { } known) return known;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                _admin = identity.Claims.Any(c => c.Value == "S-1-5-32-544");
            }
            catch { _admin = false; }
            return _admin.Value;
        }
    }

    private static int? _session;

    /// <summary>The Windows session this DeviceMon runs in; only programs in this session belong to its account.</summary>
    public static int SessionId
    {
        get
        {
            if (_session is { } known) return known;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            _session = process.SessionId;
            return _session.Value;
        }
    }

    /// <summary>True for a rule meant for everyone (no account) or for the account this DeviceMon runs for.</summary>
    public static bool AppliesHere(string? ruleSid) =>
        string.IsNullOrEmpty(ruleSid) || ruleSid == CurrentSid;

    public static bool IsValidSid(string? sid) =>
        !string.IsNullOrEmpty(sid) && sid.Length <= 100 && System.Text.RegularExpressions.Regex.IsMatch(sid, @"^S-1-\d+(-\d+){3,14}\z");
}
