namespace MonitorAndControl.Models;

/// <summary>Foreground time on one app or website during one clock hour (for the activity timeline).</summary>
public class ActivityHourRecord
{
    public int Hour { get; set; }
    /// <summary>"app" or "site".</summary>
    public string Kind { get; set; } = "app";
    public string Name { get; set; } = "";
    public long Seconds { get; set; }
}
