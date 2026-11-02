using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

/// <summary>The app allowances that apply to one Windows account alone (used by enforcement and by the time-left icon).</summary>
public static class AccountAllowances
{
    /// <summary>For each enabled account limit: the account's own time used today and its allowance (bonus minutes included).</summary>
    public static async Task<List<(string App, long Used, long Max)>> ForAccountAsync(UsageDatabase db, string sid)
    {
        var result = new List<(string App, long Used, long Max)>();
        if (string.IsNullOrEmpty(sid)) return result;
        var rules = (await db.GetUserLimitRulesAsync(sid)).Where(l => l.Enabled).ToList();
        if (rules.Count == 0) return result;
        var used = await db.GetUserAppUsageTodayAsync(sid);
        var bonus = (await db.GetTodayBonusTimeAsync()).ToDictionary(b => b.AppName, b => b.BonusMinutes, StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            used.TryGetValue(rule.AppName, out var seconds);
            bonus.TryGetValue(rule.AppName, out var extra);
            var max = (rule.DailyMaxMinutes + extra) * 60L;
            if (max > 0) result.Add((rule.AppName, seconds, max));
        }
        return result;
    }
}
