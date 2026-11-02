namespace MonitorAndControl.Models;

/// <summary>Foreground browser time on one website (host name only) on one day.</summary>
public class SiteUsageRecord
{
    public string Host { get; set; } = "";
    public DateTime Date { get; set; }
    public long Seconds { get; set; }
}
