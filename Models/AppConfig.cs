namespace MonitorAndControl.Models;

public record AppMapping(string ProcessName, string AppName);

public class AppConfig
{
    public int DashboardPort { get; set; } = 5000;
    public bool EnableRemoteDashboard { get; set; } = false;
    public string DashboardBindAddress { get; set; } = "127.0.0.1";
    public int KillDelaySeconds { get; set; } = 30;
    public bool ShowWarningOnChildPc { get; set; } = true;
    public int PollIntervalMs { get; set; } = 1000;
    public int FlushIntervalSec { get; set; } = 30;
    public string HotKeyModifiers { get; set; } = "Control+Alt";
    public string HotKeyKey { get; set; } = "H";
    public Dictionary<string, string> KnownApps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AppLimitRule> DefaultLimits { get; set; } = new();
    public List<ScheduleRule> Schedule { get; set; } = new();
}
