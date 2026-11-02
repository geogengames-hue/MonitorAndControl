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
            // 0 = blocked, N = N minutes a day, -1 = always allowed (matters when only listed sites may open)
            var blocked = rule.Enabled && (rule.DailyMaxMinutes == 0 || (rule.DailyMaxMinutes > 0 && used >= rule.DailyMaxMinutes * 60L));
            return new SiteLimitStatus(rule.Host, rule.DailyMaxMinutes, rule.Enabled, used, blocked);
        }).ToList();
    }

    /// <summary>Sites that may open in allow-list mode: every enabled rule that is not a block and has time left.</summary>
    public static List<string> AllowedHosts(IEnumerable<SiteLimitStatus> statuses) =>
        statuses.Where(s => s.Enabled && s.DailyMaxMinutes != 0 && !s.Blocked).Select(s => s.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static List<string> BlockedHosts(IEnumerable<SiteLimitStatus> statuses) =>
        statuses.Where(s => s.Blocked).Select(s => s.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
