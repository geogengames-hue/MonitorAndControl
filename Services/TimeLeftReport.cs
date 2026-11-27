using MonitorAndControl.Data;
using MonitorAndControl.ScreenTime;

namespace MonitorAndControl.Services;

public sealed record TimeLeftView(string Tooltip, List<string> Lines);

/// <summary>What the child is shown when "show time left" is on: PC time, bedtime, lock, homework mode and app allowances.</summary>
public static class TimeLeftReport
{
    public static string Duration(long seconds, string language = "en")
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1) return Localization.Text("DurationHoursMinutes", language, (int)t.TotalHours, t.Minutes);
        return t.TotalMinutes >= 1
            ? Localization.Text("DurationMinutes", language, (int)t.TotalMinutes)
            : Localization.Text("LessThanAMinute", language);
    }

    private static string FormatBedtime(int mins, string language) =>
        mins >= 60
            ? Localization.Text("DurationHoursMinutes", language, mins / 60, mins % 60)
            : Localization.Text("DurationMinutesFull", language, mins);

    private static string FormatBedtimeShort(int mins, string language) =>
        mins >= 60
            ? Localization.Text("DurationHoursMinutes", language, mins / 60, mins % 60)
            : Localization.Text("DurationMinutes", language, mins);

    /// <summary>Pure formatting, so it can be tested without a database or a screen.</summary>
    public static TimeLeftView Build(long? pcRemainingSeconds, int? minutesToBedtime, bool inBedtime, DateTimeOffset? lockedUntil,
        DateTimeOffset? homeworkUntil, IEnumerable<(string App, long Used, long Max)> apps, DateTimeOffset now, string language = "en")
    {
        var lines = new List<string>();
        var tip = new List<string>();

        if (lockedUntil is { } locked && locked > now)
            lines.Add(Localization.Text("PcLockedUntil", language, $"{ParentClock.ToRuleTime(locked):HH:mm}"));
        if (pcRemainingSeconds is { } left)
        {
            lines.Add(Localization.Text("PcTimeLeftToday", language, Duration(left, language)));
            tip.Add(Localization.Text("PcTimeLeftTooltip", language, Duration(left, language)));
        }
        if (inBedtime) lines.Add(Localization.Text("ItIsBedtime", language));
        else if (minutesToBedtime is { } mins)
        {
            lines.Add(Localization.Text("BedtimeIn", language, FormatBedtime(mins, language)));
            if (mins <= 120) tip.Add(Localization.Text("BedtimeInTooltip", language, FormatBedtimeShort(mins, language)));
        }
        if (homeworkUntil is { } hw && hw > now)
        {
            lines.Add(Localization.Text("HomeworkUntil", language, $"{ParentClock.ToRuleTime(hw):HH:mm}"));
            tip.Add(Localization.Text("HomeworkUntilTooltip", language, $"{ParentClock.ToRuleTime(hw):HH:mm}"));
        }

        var rows = apps.OrderBy(a => a.Max - a.Used).ToList();
        foreach (var (app, used, max) in rows)
        {
            var remaining = Math.Max(0, max - used);
            lines.Add(remaining == 0
                ? Localization.Text("AppNoTimeLeftToday", language, app, Duration(used, language))
                : Localization.Text("AppTimeLeftOfMax", language, app, Duration(remaining, language), Duration(max, language)));
        }
        if (rows.Count > 0)
        {
            var first = rows[0];
            tip.Add(Localization.Text("AppTimeLeftTooltip", language, first.App, Duration(Math.Max(0, first.Max - first.Used), language)));
        }

        if (lines.Count == 0) lines.Add(Localization.Text("NoTimeLimitsSet", language));
        var tooltip = tip.Count > 0 ? string.Join(" | ", tip) : Localization.Text("NoTimeLimitsRightNow", language);
        return new TimeLeftView(tooltip.Length > 120 ? tooltip[..117] + "..." : tooltip, lines);
    }

    /// <summary>Gathers the numbers for the account this DeviceMon is running for.</summary>
    public static async Task<TimeLeftView> CollectAsync(UsageDatabase db, string language = "en")
    {
        var now = DateTimeOffset.UtcNow;
        var sid = AccountIdentity.CurrentSid;
        long? pcRemaining = null;
        int? untilBed = null;
        var inBed = false;
        DateTimeOffset? lockedUntil = null;

        try
        {
            var policy = sid.Length > 0 ? ScreenTimeFiles.ReadPolicies().FirstOrDefault(p => p.UserSid == sid && p.Enabled) : null;
            if (policy != null)
            {
                untilBed = ScreenTimeRules.MinutesUntilBedtime(policy, ParentClock.Now);
                inBed = ScreenTimeRules.InBedtime(policy, ParentClock.Now);
                if (ScreenTimeRules.IsLocked(policy, now)) lockedUntil = policy.LockUntilUtc;
                // The service keeps the running counters; read this account's remaining time from its status file.
                if (File.Exists(ScreenTimeFiles.StatusPath))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ScreenTimeFiles.StatusPath));
                    if (doc.RootElement.TryGetProperty("profiles", out var profiles))
                        foreach (var p in profiles.EnumerateArray())
                            if (p.TryGetProperty("userSid", out var u) && u.GetString() == sid && p.TryGetProperty("remainingSeconds", out var r))
                                pcRemaining = r.GetInt64();
                }
            }
        }
        catch { /* the service may not be installed; show what is known */ }

        var apps = new List<(string App, long Used, long Max)>();
        try
        {
            var bonus = (await db.GetTodayBonusTimeAsync()).ToDictionary(b => b.AppName, b => b.BonusMinutes, StringComparer.OrdinalIgnoreCase);
            var shared = await db.GetTodayUsageAsync();
            foreach (var l in (await db.GetLimitRulesAsync()).Where(l => l.Enabled))
            {
                bonus.TryGetValue(l.AppName, out var extra);
                var used = shared.FirstOrDefault(u => u.AppName.Equals(l.AppName, StringComparison.OrdinalIgnoreCase))?.TotalSeconds ?? 0;
                apps.Add((l.AppName, used, (l.DailyMaxMinutes + extra) * 60L));
            }
            apps.AddRange(await AccountAllowances.ForAccountAsync(db, sid));
        }
        catch { /* the database may be busy; the next refresh will fill it in */ }

        var homework = HomeworkState.Read();
        return Build(pcRemaining, untilBed, inBed, lockedUntil, HomeworkState.IsActive(homework, now) ? homework!.UntilUtc : null, apps, now, language);
    }
}
