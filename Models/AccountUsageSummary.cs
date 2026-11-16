namespace MonitorAndControl.Models;

/// <summary>What one Windows account has used today, for the account overview on the Today page.</summary>
public class AccountUsageSummary
{
    public string UserSid { get; set; } = "";
    public long TotalSeconds { get; set; }
    public int AppCount { get; set; }
    public string TopApp { get; set; } = "";
    public long TopAppSeconds { get; set; }
}
