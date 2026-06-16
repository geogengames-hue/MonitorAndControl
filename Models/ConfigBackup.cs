namespace MonitorAndControl.Models;

public class ConfigBackup
{
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public List<AppMapping> AppMappings { get; set; } = new();
    public List<AppLimitRule> Limits { get; set; } = new();
    public List<ScheduleRule> Schedules { get; set; } = new();
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
