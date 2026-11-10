using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

/// <summary>
/// Whether the parent excluded the Windows account this DeviceMon runs for from app limits, groups, the allowed-hours
/// schedule and homework app closing. The list is kept in the database; each DeviceMon refreshes its own flag.
/// </summary>
public static class AccountExemption
{
    private static volatile bool _exempt;

    public static bool IsExempt => _exempt;

    public static async Task RefreshAsync(UsageDatabase db)
    {
        var sid = AccountIdentity.CurrentSid;
        _exempt = sid.Length > 0 && (await db.GetExemptAccountsAsync()).Contains(sid, StringComparer.OrdinalIgnoreCase);
    }

    public static void Set(bool exempt) => _exempt = exempt;
}
