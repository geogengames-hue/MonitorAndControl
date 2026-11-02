using System.Globalization;
using System.Text;
using MonitorAndControl.Models;

namespace MonitorAndControl.Services;

public sealed record WeeklyDay(string Date, long ThisWeekSeconds, long LastWeekSeconds);

public sealed record WeeklyApp(string App, long ThisWeekSeconds, long LastWeekSeconds);

public sealed class WeeklyReportData
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string PreviousFrom { get; init; } = "";
    public string PreviousTo { get; init; } = "";
    public long ThisWeekSeconds { get; init; }
    public long LastWeekSeconds { get; init; }
    /// <summary>Change from last week in percent; null when last week had no usage to compare with.</summary>
    public double? ChangePercent { get; init; }
    public List<WeeklyDay> Days { get; init; } = new();
    public List<WeeklyApp> Apps { get; init; } = new();
}

/// <summary>
/// Compares the last 7 days with the 7 days before them, per day and per app. Only time an app
/// was in the foreground (or not classified, in older records) counts: background time overlaps
/// with other apps, so adding it up would make a day look longer than it was.
/// </summary>
public static class WeeklyReport
{
    private const int MaxApps = 10;

    public static WeeklyReportData Build(IEnumerable<AppUsageRecord> records, DateTime endDate)
    {
        var end = endDate.Date;
        var thisStart = end.AddDays(-6);
        var lastStart = thisStart.AddDays(-7);
        var lastEnd = thisStart.AddDays(-1);

        var byDay = new Dictionary<DateTime, long>();
        var thisApps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var lastApps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
        {
            var day = r.Date.Date;
            if (day < lastStart || day > end) continue;
            var seconds = r.ForegroundSeconds + r.UnclassifiedSeconds;
            byDay[day] = byDay.GetValueOrDefault(day) + seconds;
            var target = day >= thisStart ? thisApps : lastApps;
            target[r.AppName] = target.GetValueOrDefault(r.AppName) + seconds;
        }

        var days = Enumerable.Range(0, 7).Select(i =>
        {
            var d = thisStart.AddDays(i);
            return new WeeklyDay(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                byDay.GetValueOrDefault(d), byDay.GetValueOrDefault(d.AddDays(-7)));
        }).ToList();

        var thisTotal = days.Sum(d => d.ThisWeekSeconds);
        var lastTotal = days.Sum(d => d.LastWeekSeconds);
        var apps = thisApps.Keys.Union(lastApps.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(a => new WeeklyApp(a, thisApps.GetValueOrDefault(a), lastApps.GetValueOrDefault(a)))
            .OrderByDescending(a => a.ThisWeekSeconds).ThenByDescending(a => a.LastWeekSeconds)
            .Take(MaxApps).ToList();

        return new WeeklyReportData
        {
            From = thisStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            To = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            PreviousFrom = lastStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            PreviousTo = lastEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ThisWeekSeconds = thisTotal,
            LastWeekSeconds = lastTotal,
            ChangePercent = Percent(thisTotal, lastTotal),
            Days = days,
            Apps = apps
        };
    }

    public static double? Percent(long current, long previous) =>
        previous <= 0 ? null : Math.Round((current - previous) * 100.0 / previous, 0);

    public static string Duration(long seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalHours}h {t.Minutes}m";
    }

    public static string DescribeChange(double? percent) => percent switch
    {
        null => "no usage to compare with",
        0 => "same as before",
        > 0 => $"+{percent:0}%",
        _ => $"{percent:0}%"
    };

    /// <summary>Plain-text block for the summary email.</summary>
    public static string ToText(WeeklyReportData data)
    {
        var text = new StringBuilder();
        text.AppendLine($"Last 7 days ({data.From} to {data.To}) compared with the 7 days before:")
            .AppendLine($"Total: {Duration(data.ThisWeekSeconds)} vs {Duration(data.LastWeekSeconds)} ({DescribeChange(data.ChangePercent)})")
            .AppendLine();
        foreach (var d in data.Days)
            text.AppendLine($"{d.Date}: {Duration(d.ThisWeekSeconds)} (a week earlier {Duration(d.LastWeekSeconds)})");
        if (data.Apps.Count > 0)
        {
            text.AppendLine().AppendLine("Apps (this week vs last week):");
            foreach (var a in data.Apps.Take(5))
                text.AppendLine($"{a.App}: {Duration(a.ThisWeekSeconds)} vs {Duration(a.LastWeekSeconds)} ({DescribeChange(Percent(a.ThisWeekSeconds, a.LastWeekSeconds))})");
        }
        return text.ToString();
    }
}
