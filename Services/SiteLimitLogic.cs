using MonitorAndControl.Models;
using MonitorAndControl.ScreenTime;

namespace MonitorAndControl.Services;

public sealed record SiteLimitStatus(string Host, int DailyMaxMinutes, bool Enabled, long UsedSeconds, bool Blocked);

public static class SiteLimitLogic
{
    /// <summary>Works out, for each rule, today's time on that site (and its subdomains) and whether it is blocked now.</summary>
    public static List<SiteLimitStatus> Evaluate(IEnumerable<SiteLimit> rules, IEnumerable<SiteUsageRecord> usageToday)
    {
        var usage = usageToday.ToList();
        return rules.Select(rule =>
        {
            var used = usage.Where(u => SiteBlocking.Covers(rule.Host, u.Host)).Sum(u => u.Seconds);
            var blocked = rule.Enabled && (rule.DailyMaxMinutes == 0 || used >= rule.DailyMaxMinutes * 60L);
            return new SiteLimitStatus(rule.Host, rule.DailyMaxMinutes, rule.Enabled, used, blocked);
        }).ToList();
    }

    public static List<string> BlockedHosts(IEnumerable<SiteLimitStatus> statuses) =>
        statuses.Where(s => s.Blocked).Select(s => s.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
