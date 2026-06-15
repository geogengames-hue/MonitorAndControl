namespace MonitorAndControl.Models;

public class AppLimitRule
{
    public int Id { get; set; }
    public string AppName { get; set; } = "";
    public int DailyMaxMinutes { get; set; } = 120;
    public bool Enabled { get; set; } = true;
}
