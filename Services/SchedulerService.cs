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
        var now = DateTime.Now;
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

    public async Task<List<ScheduleRule>> GetViolatingRulesAsync()
    {
        var rules = await GetRulesAsync();
        return rules.Where(r => r.Enabled && IsOutsideAllowedHours(r)).ToList();
    }
}
