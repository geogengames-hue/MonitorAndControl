namespace MonitorAndControl.Models;

using System.Text.Json.Serialization;

public class AppLimitGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int DailyMaxMinutes { get; set; } = 180;
    public bool Enabled { get; set; } = true;
    public List<string> AppNames { get; set; } = new();
    [JsonIgnore]
    public long TodaySeconds { get; set; }
}
