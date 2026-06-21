namespace MonitorAndControl.Models;

public class AppUsageRecord
{
    public string AppName { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public DateTime Date { get; set; }
    public long TotalSeconds { get; set; }
    public long ForegroundSeconds { get; set; }
    public long BackgroundSeconds { get; set; }
    public long UnclassifiedSeconds => Math.Max(0, TotalSeconds - ForegroundSeconds - BackgroundSeconds);
    public string DurationFormatted
    {
        get => FormatDuration(TotalSeconds);
    }
    public string ForegroundDurationFormatted => FormatDuration(ForegroundSeconds);
    public string BackgroundDurationFormatted => FormatDuration(BackgroundSeconds);
    public string UnclassifiedDurationFormatted => FormatDuration(UnclassifiedSeconds);

    private static string FormatDuration(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
            : $"{ts.Minutes}m {ts.Seconds}s";
    }
}
