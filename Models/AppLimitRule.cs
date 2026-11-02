namespace MonitorAndControl.Models;

public class AppLimitRule
{
    public int Id { get; set; }
    public string AppName { get; set; } = "";
    public int DailyMaxMinutes { get; set; } = 120;
    public bool Enabled { get; set; } = true;
    /// <summary>Empty = applies to everyone (shared total); otherwise only this Windows account (SID), counted on its own.</summary>
    public string UserSid { get; set; } = "";
}
