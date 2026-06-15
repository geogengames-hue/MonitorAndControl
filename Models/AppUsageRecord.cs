namespace MonitorAndControl.Models;

public class AppUsageRecord
{
    public string AppName { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public DateTime Date { get; set; }
    public long TotalSeconds { get; set; }
    public string DurationFormatted
    {
        get
        {
            var ts = TimeSpan.FromSeconds(TotalSeconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
                : $"{ts.Minutes}m {ts.Seconds}s";
        }
    }
}
