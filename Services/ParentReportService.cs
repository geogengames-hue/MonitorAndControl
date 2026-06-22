using System.ServiceProcess;
using System.Text;
using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

public sealed class ParentReportService : IDisposable
{
    private readonly UsageDatabase _db;
    private readonly EmailService _email;
    private readonly NotificationService _notifications;
    private readonly string _configPath;
    private readonly System.Threading.Timer _timer;
    private readonly HashSet<string> _activeTamperConditions = new(StringComparer.OrdinalIgnoreCase);
    private long _lastTick;
    private DateTime _lastUtc;
    private int _checking;

    public ParentReportService(UsageDatabase db, EmailService email, NotificationService notifications, string configPath)
    {
        _db = db;
        _email = email;
        _notifications = notifications;
        _configPath = configPath;
        _lastTick = Environment.TickCount64;
        _lastUtc = DateTime.UtcNow;
        _timer = new System.Threading.Timer(Check, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));

    public async Task ReloadAndCheckAsync() => await CheckAsync();

    public void ReportLoginLockout(string source) => _ = SendTamperAlertAsync(
        "login_lockout",
        $"Repeated failed dashboard logins from {source} triggered a temporary lockout.");

    private async void Check(object? state)
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        try { await CheckAsync(); }
        catch (Exception ex) { Logger.Instance.Error($"Parent report check failed: {ex.Message}"); }
        finally { Volatile.Write(ref _checking, 0); }
    }

    private async Task CheckAsync()
    {
        await CheckScheduledSummaryAsync(DateTime.Now);
        await CheckTamperConditionsAsync();
    }

    private async Task CheckScheduledSummaryAsync(DateTime now)
    {
        if ((await _db.GetSettingAsync("SummaryEnabled", "false")) != "true") return;
        var frequency = (await _db.GetSettingAsync("SummaryFrequency", "weekly")).ToLowerInvariant();
        var timeText = await _db.GetSettingAsync("SummaryTime", "18:00");
        if (!TimeSpan.TryParse(timeText, out var time)) time = TimeSpan.FromHours(18);
        _ = int.TryParse(await _db.GetSettingAsync("SummaryWeeklyDay", "0"), out var weeklyDay);
        _ = int.TryParse(await _db.GetSettingAsync("SummaryMonthlyDay", "1"), out var monthlyDay);
        var due = GetLatestDue(now, frequency, time, weeklyDay, monthlyDay);
        var lastSentText = await _db.GetSettingAsync("SummaryLastSentDue", "");
        if (DateTime.TryParse(lastSentText, out var lastSent) && lastSent >= due) return;

        var from = GetPeriodStart(due, frequency, time, weeklyDay, monthlyDay);
        var records = await _db.GetUsageRangeAsync(from.Date, due.AddTicks(-1).Date);
        var grouped = records.GroupBy(r => r.AppName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                App = g.Key,
                Total = g.Sum(x => x.TotalSeconds),
                Foreground = g.Sum(x => x.ForegroundSeconds),
                Background = g.Sum(x => x.BackgroundSeconds)
            })
            .OrderByDescending(x => x.Total)
            .ToList();
        var body = new StringBuilder()
            .AppendLine($"Usage summary: {from:g} - {due:g}")
            .AppendLine($"Computer: {Environment.MachineName}")
            .AppendLine();
        if (grouped.Count == 0) body.AppendLine("No tracked usage in this period.");
        foreach (var item in grouped.Take(15))
            body.AppendLine($"{item.App}: {Format(item.Total)} (foreground {Format(item.Foreground)}, background {Format(item.Background)})");

        var error = await _email.SendSystemEmailAsync($"{frequency[..1].ToUpperInvariant() + frequency[1..]} Usage Summary", body.ToString());
        if (error == null)
            await _db.SetSettingAsync("SummaryLastSentDue", due.ToString("O"));
    }

    public static DateTime GetLatestDue(DateTime now, string frequency, TimeSpan time, int weeklyDay, int monthlyDay)
    {
        if (frequency == "daily")
        {
            var due = now.Date + time;
            return due <= now ? due : due.AddDays(-1);
        }
        if (frequency == "monthly")
        {
            var day = Math.Clamp(monthlyDay, 1, DateTime.DaysInMonth(now.Year, now.Month));
            var due = new DateTime(now.Year, now.Month, day) + time;
            if (due <= now) return due;
            var previous = now.AddMonths(-1);
            return new DateTime(previous.Year, previous.Month,
                Math.Clamp(monthlyDay, 1, DateTime.DaysInMonth(previous.Year, previous.Month))) + time;
        }
        var target = (DayOfWeek)Math.Clamp(weeklyDay, 0, 6);
        var daysBack = ((int)now.DayOfWeek - (int)target + 7) % 7;
        var weeklyDue = now.Date.AddDays(-daysBack) + time;
        return weeklyDue <= now ? weeklyDue : weeklyDue.AddDays(-7);
    }

    private static DateTime GetPeriodStart(DateTime due, string frequency, TimeSpan time, int weeklyDay, int monthlyDay)
    {
        if (frequency == "daily") return due.AddDays(-1);
        if (frequency != "monthly") return due.AddDays(-7);
        var previous = due.AddMonths(-1);
        return new DateTime(previous.Year, previous.Month,
            Math.Clamp(monthlyDay, 1, DateTime.DaysInMonth(previous.Year, previous.Month))) + time;
    }

    private async Task CheckTamperConditionsAsync()
    {
        if ((await _db.GetSettingAsync("TamperAlertsEnabled", "false")) != "true") return;
        await SetConditionAsync("database_missing", !File.Exists(_db.DatabasePath), "The monitoring database file is missing.");
        await SetConditionAsync("config_missing", !File.Exists(_configPath), "The appsettings.json configuration file is missing.");

        var nowTick = Environment.TickCount64;
        var nowUtc = DateTime.UtcNow;
        var expected = TimeSpan.FromMilliseconds(nowTick - _lastTick);
        var actual = nowUtc - _lastUtc;
        _lastTick = nowTick;
        _lastUtc = nowUtc;
        await SetConditionAsync("clock_changed", Math.Abs((actual - expected).TotalMinutes) >= 5,
            $"The system clock changed unexpectedly by approximately {(actual - expected).TotalMinutes:F0} minutes.");

        var watchdogStopped = false;
        try
        {
            using var service = new ServiceController("GameHost");
            watchdogStopped = service.Status is not ServiceControllerStatus.Running and not ServiceControllerStatus.StartPending;
        }
        catch { watchdogStopped = true; }
        await SetConditionAsync("watchdog_unavailable", watchdogStopped, "The GameHost watchdog service is stopped or unavailable.");
    }

    private async Task SetConditionAsync(string key, bool active, string message)
    {
        if (!active) { _activeTamperConditions.Remove(key); return; }
        if (_activeTamperConditions.Add(key)) await SendTamperAlertAsync(key, message);
    }

    private async Task SendTamperAlertAsync(string type, string message)
    {
        if ((await _db.GetSettingAsync("TamperAlertsEnabled", "false")) != "true") return;
        Logger.Instance.Warn($"Tamper alert: {message}");
        await _email.SendSystemEmailAsync($"Tamper Alert: {type}", message);
        await _notifications.NotifySystemAsync("tamper_alert", message);
    }

    private static string Format(long seconds) => TimeSpan.FromSeconds(seconds) is var value
        ? $"{(int)value.TotalHours}h {value.Minutes}m"
        : "0m";

    public void Dispose() => _timer.Dispose();
}
