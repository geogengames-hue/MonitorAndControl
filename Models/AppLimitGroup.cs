namespace MonitorAndControl.Models;

using System.Text.Json.Serialization;

public class AppLimitGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int DailyMaxMinutes { get; set; } = 180;
    public bool Enabled { get; set; } = true;
    public List<string> AppNames { get; set; } = new();
    /// <summary>Empty = applies to everyone; otherwise only this Windows account (SID).</summary>
    public string UserSid { get; set; } = "";
    /// <summary>Empty = only the listed apps; otherwise also every other app in this category (see AppCategories).</summary>
    public string Category { get; set; } = "";
    [JsonIgnore]
    public long TodaySeconds { get; set; }
}
