using System.Text.Json;

namespace MonitorAndControl.ScreenTime;

public sealed class ScreenTimePolicy
{
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");
    public string UserSid { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int DailyMinutes { get; set; } = 180;
    public bool Enabled { get; set; } = true;
    public string BonusDay { get; set; } = "";
    public int BonusMinutes { get; set; }
    public void Validate()
    {
        if (!Guid.TryParseExact(ProfileId, "N", out _) || !UserSid.StartsWith("S-1-5-21-", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 100
            || DailyMinutes is < 1 or > 1440 || BonusMinutes is < 0 or > 1440)
            throw new InvalidDataException("Invalid screen-time policy.");
    }
}

public sealed class ScreenTimeUsage
{
    public string Day { get; set; } = "";
    public double Seconds { get; set; }
}

public static class ScreenTimeRules
{
    public static long Remaining(ScreenTimePolicy policy, ScreenTimeUsage usage, DateOnly today)
    {
        policy.Validate();
        var day = today.ToString("yyyy-MM-dd");
        var bonus = policy.BonusDay == day ? policy.BonusMinutes : 0;
        return Math.Max(0, (policy.DailyMinutes + bonus) * 60L - (long)Math.Ceiling(usage.Seconds));
    }

    // A clock rollback must not grant another allowance. A later calendar day resets once.
    public static void Advance(ScreenTimeUsage usage, DateOnly today, double seconds)
    {
        var day = today.ToString("yyyy-MM-dd");
        if (string.CompareOrdinal(day, usage.Day) > 0)
        {
            usage.Day = day;
            usage.Seconds = 0;
        }
        if (!double.IsFinite(usage.Seconds) || usage.Seconds < 0)
            throw new InvalidDataException("Invalid screen-time usage.");
        usage.Seconds += double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 5) : 0;
    }
}

public static class ScreenTimeFiles
{
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeviceMonScreenTime");
    public static string PoliciesPath => Path.Combine(DirectoryPath, "policies.json");
    public static string UsagePath => Path.Combine(DirectoryPath, "usage.json");
    public static string StatusPath => Path.Combine(DirectoryPath, "status.json");
    public static List<ScreenTimePolicy> ReadPolicies()
    {
        if (!File.Exists(PoliciesPath)) return new();
        var policies = JsonSerializer.Deserialize<List<ScreenTimePolicy>>(File.ReadAllText(PoliciesPath))
            ?? throw new InvalidDataException("Missing policies.");
        foreach (var policy in policies) policy.Validate();
        if (policies.Select(p => p.UserSid).Distinct().Count() != policies.Count
            || policies.Select(p => p.ProfileId).Distinct().Count() != policies.Count)
            throw new InvalidDataException("Duplicate screen-time account or profile.");
        return policies;
    }
    public static void WriteAtomic<T>(string path, T value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
