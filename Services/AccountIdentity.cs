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

    public static bool IsValidSid(string? sid) =>
        !string.IsNullOrEmpty(sid) && sid.Length <= 100 && System.Text.RegularExpressions.Regex.IsMatch(sid, @"^S-1-\d+(-\d+){3,14}\z");
}
