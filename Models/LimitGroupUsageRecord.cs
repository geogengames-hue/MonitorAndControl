namespace MonitorAndControl.Models;

public class LimitGroupUsageRecord
{
    public int GroupId { get; set; }
    public string GroupName { get; set; } = "";
    public DateTime Date { get; set; }
    public long TotalSeconds { get; set; }
    public string DurationFormatted
    {
        get
        {
            var duration = TimeSpan.FromSeconds(TotalSeconds);
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
                : $"{duration.Minutes}m {duration.Seconds}s";
        }
    }
}
