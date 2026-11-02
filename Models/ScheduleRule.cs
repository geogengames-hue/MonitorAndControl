namespace MonitorAndControl.Models;

public class ScheduleRule
{
    public int Id { get; set; }
    public string AppName { get; set; } = "";
    public string DayOfWeek { get; set; } = "";
    public string StartTime { get; set; } = "09:00";
    public string EndTime { get; set; } = "21:00";
    public bool Enabled { get; set; } = true;
    /// <summary>Empty = applies to everyone; otherwise only this Windows account (SID).</summary>
    public string UserSid { get; set; } = "";
}
