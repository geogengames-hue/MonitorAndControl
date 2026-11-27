using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

/// <summary>The app allowances that apply to one Windows account (used by enforcement and by the time-left icon).</summary>
public static class AccountAllowances
{
    /// <summary>
    /// For each enabled limit that applies to the account: the account's own time used today and its allowance
    /// (bonus minutes included). A limit set for everyone gives every account its own allowance, counted from that
    /// account's usage alone; a limit set for the account itself replaces the shared one for that app.
    /// Without a known account the shared total is used.
    /// </summary>
    public static async Task<List<(string App, long Used, long Max)>> ForAccountAsync(UsageDatabase db, string sid)
    {
        var result = new List<(string App, long Used, long Max)>();
        var own = string.IsNullOrEmpty(sid)
            ? new List<Models.AppLimitRule>()
            : (await db.GetUserLimitRulesAsync(sid)).Where(l => l.Enabled).ToList();
        var shared = (await db.GetLimitRulesAsync())
            .Where(l => l.Enabled && !own.Any(o => o.AppName.Equals(l.AppName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (own.Count == 0 && shared.Count == 0) return result;

        Dictionary<string, long> used;
        if (string.IsNullOrEmpty(sid))
        {
            used = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in await db.GetTodayUsageAsync())
                used[record.AppName] = used.GetValueOrDefault(record.AppName) + record.TotalSeconds;
        }
        else
            used = await db.GetUserAppUsageTodayAsync(sid);

        var bonus = await BonusForAccountAsync(db, sid);
        foreach (var rule in shared.Concat(own))
        {
            used.TryGetValue(rule.AppName, out var seconds);
            bonus.TryGetValue(rule.AppName, out var extra);
            var max = (rule.DailyMaxMinutes + extra) * 60L;
            if (max > 0) result.Add((rule.AppName, seconds, max));
        }
        return result;
    }

    /// <summary>Today's extra minutes per app for the account: those given to everyone plus those given to it alone.</summary>
    public static async Task<Dictionary<string, int>> BonusForAccountAsync(UsageDatabase db, string sid)
    {
        var bonus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in await db.GetTodayBonusTimeAsync())
            bonus[b.AppName] = bonus.GetValueOrDefault(b.AppName) + b.BonusMinutes;
        if (!string.IsNullOrEmpty(sid))
            foreach (var (app, minutes) in await db.GetUserBonusTodayAsync(sid))
                bonus[app] = bonus.GetValueOrDefault(app) + minutes;
        return bonus;
    }
}
