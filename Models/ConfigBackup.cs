namespace MonitorAndControl.Models;

public class ConfigBackup
{
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public List<AppMapping> AppMappings { get; set; } = new();
    public List<AppLimitRule> Limits { get; set; } = new();
    public List<AppLimitGroup> LimitGroups { get; set; } = new();
    public List<ScheduleRule> Schedules { get; set; } = new();
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Added in 1.13. Missing from older backups (null): importing such a backup leaves these rules as they are.
    public List<AppLimitRule>? UserLimits { get; set; }
    public List<SiteLimit>? SiteLimits { get; set; }
    public List<SiteLimit>? UserSiteLimits { get; set; }
    public List<string>? HomeworkApps { get; set; }
    public List<string>? AlwaysAllowedApps { get; set; }
    public Dictionary<string, string>? AppCategories { get; set; }
}
