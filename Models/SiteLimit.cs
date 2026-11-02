namespace MonitorAndControl.Models;

/// <summary>A website limit: DailyMaxMinutes of browsing per day, or 0 to block the site entirely.</summary>
public class SiteLimit
{
    public string Host { get; set; } = "";
    public int DailyMaxMinutes { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Empty = applies to everyone; otherwise only this Windows account (SID).</summary>
    public string UserSid { get; set; } = "";
}
