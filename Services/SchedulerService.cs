using MonitorAndControl.Data;
using MonitorAndControl.Models;

namespace MonitorAndControl.Services;

public class SchedulerService
{
    private readonly UsageDatabase _db;
    private List<ScheduleRule> _cachedRules = new();
    private DateTime _lastCacheUpdate = DateTime.MinValue;

    public SchedulerService(UsageDatabase db)
    {
        _db = db;
    }

    public async Task<List<ScheduleRule>> GetRulesAsync()
    {
        if ((DateTime.UtcNow - _lastCacheUpdate).TotalMinutes > 1)
        {
            _cachedRules = await _db.GetScheduleRulesAsync();
            _lastCacheUpdate = DateTime.UtcNow;
        }
        return _cachedRules;
    }

    public void InvalidateCache()
    {
        _lastCacheUpdate = DateTime.MinValue;
    }

    public bool IsOutsideAllowedHours(ScheduleRule rule)
    {
        return IsOutsideAllowedHours(rule, DateTime.Now);
    }

    public static bool IsOutsideAllowedHours(ScheduleRule rule, DateTime now)
    {
        var today = now.DayOfWeek;

        bool matchesDay = rule.DayOfWeek.ToLowerInvariant() switch
        {
            "weekday" => today >= DayOfWeek.Monday && today <= DayOfWeek.Friday,
            "weekend" => today == DayOfWeek.Saturday || today == DayOfWeek.Sunday,
            "monday" => today == DayOfWeek.Monday,
            "tuesday" => today == DayOfWeek.Tuesday,
            "wednesday" => today == DayOfWeek.Wednesday,
            "thursday" => today == DayOfWeek.Thursday,
            "friday" => today == DayOfWeek.Friday,
            "saturday" => today == DayOfWeek.Saturday,
            "sunday" => today == DayOfWeek.Sunday,
            "everyday" => true,
            _ => false
        };

        if (!matchesDay) return false;

        if (TimeSpan.TryParse(rule.StartTime, out var start) &&
            TimeSpan.TryParse(rule.EndTime, out var end))
        {
            var currentTime = now.TimeOfDay;
            if (end > start)
                return currentTime < start || currentTime >= end;
            else
                return currentTime >= end && currentTime < start;
        }

        return false;
    }

    public async Task<bool> IsInViolationPeriodAsync()
    {
        var rules = await GetRulesAsync();
        return rules.Any(r => r.Enabled && IsOutsideAllowedHours(r));
    }

    public async Task<HashSet<string>> GetViolatingAppNamesAsync(HashSet<string> knownAppNames)
    {
        var rules = await GetRulesAsync();
        var violating = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules.Where(r => r.Enabled && IsOutsideAllowedHours(r)))
        {
            if (string.IsNullOrWhiteSpace(rule.AppName))
            {
                violating.UnionWith(knownAppNames);
                continue;
            }

            violating.Add(rule.AppName);
        }

        return violating;
    }

    public async Task<List<ScheduleRule>> GetViolatingRulesAsync()
    {
        var rules = await GetRulesAsync();
        return rules.Where(r => r.Enabled && IsOutsideAllowedHours(r)).ToList();
    }

    public static DateTime? GetNextAllowedTime(IEnumerable<ScheduleRule> rules, DateTime now)
    {
        var enabled = rules.Where(r => r.Enabled).ToList();
        if (enabled.Count == 0)
            return null;

        for (var dayOffset = 0; dayOffset <= 14; dayOffset++)
        {
            var date = now.Date.AddDays(dayOffset);
            foreach (var rule in enabled)
            {
                if (!MatchesDay(rule, date.DayOfWeek))
                    continue;
                if (!TimeSpan.TryParse(rule.StartTime, out var start) ||
                    !TimeSpan.TryParse(rule.EndTime, out var end))
                    continue;

                var candidate = date.Add(start);
                if (end <= start && now.TimeOfDay < end && dayOffset == 0)
                    return now;
                if (candidate > now)
                    return candidate;
                if (dayOffset == 0 && end > start && now.TimeOfDay >= start && now.TimeOfDay < end)
                    return now;
                if (dayOffset == 0 && end <= start && now.TimeOfDay >= start)
                    return now;
            }
        }

        return null;
    }

    public static DateTime? GetCurrentAllowedWindowEnd(IEnumerable<ScheduleRule> rules, string appName, DateTime now)
    {
        var matching = rules
            .Where(r => r.Enabled &&
                (string.IsNullOrWhiteSpace(r.AppName) ||
                 r.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        DateTime? bestEnd = null;
        foreach (var rule in matching)
        {
            if (!MatchesDay(rule, now.DayOfWeek))
                continue;
            if (!TimeSpan.TryParse(rule.StartTime, out var start) ||
                !TimeSpan.TryParse(rule.EndTime, out var end))
                continue;

            var current = now.TimeOfDay;
            var inside = end > start
                ? current >= start && current < end
                : current >= start || current < end;
            if (!inside)
                continue;

            var endDate = now.Date.Add(end);
            if (end <= start && current >= start)
                endDate = endDate.AddDays(1);

            if (bestEnd == null || endDate > bestEnd)
                bestEnd = endDate;
        }

        return bestEnd;
    }

    private static bool MatchesDay(ScheduleRule rule, DayOfWeek today) =>
        rule.DayOfWeek.ToLowerInvariant() switch
        {
            "weekday" => today >= DayOfWeek.Monday && today <= DayOfWeek.Friday,
            "weekend" => today == DayOfWeek.Saturday || today == DayOfWeek.Sunday,
            "monday" => today == DayOfWeek.Monday,
            "tuesday" => today == DayOfWeek.Tuesday,
            "wednesday" => today == DayOfWeek.Wednesday,
            "thursday" => today == DayOfWeek.Thursday,
            "friday" => today == DayOfWeek.Friday,
            "saturday" => today == DayOfWeek.Saturday,
            "sunday" => today == DayOfWeek.Sunday,
            "everyday" => true,
            _ => false
        };
}
