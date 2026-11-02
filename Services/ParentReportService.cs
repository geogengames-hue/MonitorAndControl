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
    private int _guarding;
    private readonly System.Threading.Timer _guardTimer;

    public ParentReportService(UsageDatabase db, EmailService email, NotificationService notifications, string configPath)
    {
        _db = db;
        _email = email;
        _notifications = notifications;
        _configPath = configPath;
        _lastTick = Environment.TickCount64;
        _lastUtc = DateTime.UtcNow;
        _timer = new System.Threading.Timer(Check, null, Timeout.Infinite, Timeout.Infinite);
        _guardTimer = new System.Threading.Timer(GuardCheck, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _timer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
        _guardTimer.Change(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10));
    }

    // When the parent sealed the limits (service-held copy), the database must match it. This also
    // holds after a restart, so closing DeviceMon first and editing the database no longer helps.
    private async Task<string?> CheckSealedLimitsAsync()
    {
        var file = ScreenTime.LimitsSealStore.Read();
        var phase = ScreenTime.LimitsSealStore.Phase(file, DateTimeOffset.UtcNow);
        if (phase == ScreenTime.SealPhase.Off) return null;
        _db.ResetLimitsBaseline(); // the in-process guard resumes cleanly once sealing ends

        if (phase == ScreenTime.SealPhase.Editing) return null;
        var current = await _db.ReadLimitsSnapshotAsync();
        if (phase == ScreenTime.SealPhase.SealNow)
        {
            var reply = await ScreenTimeClient.SendAsync(new ScreenTime.ScreenTimeRequest
            {
                Command = "limits-seal",
                LimitsJson = System.Text.Json.JsonSerializer.Serialize(current)
            }, CancellationToken.None);
            Logger.Instance.Info(reply.Ok ? "App limits sealed after editing." : "Could not seal the app limits: " + reply.Error);
            return null;
        }

        if (file?.Content == null || current.Fingerprint() == file.Fingerprint) return null;
        await _db.ApplyLimitsSnapshotAsync(file.Content);
        return "The app limits, schedule or groups were changed without approval and have been put back from the sealed copy. " +
               "To change them on purpose, open editing under PC Time > Protect limits.";
    }

    // Checked far more often than the minute timer so a legitimate edit and an outside edit rarely share a window.
    private async void GuardCheck(object? state)
    {
        if (Interlocked.Exchange(ref _guarding, 1) != 0) return;
        try
        {
            var problem = await CheckSealedLimitsAsync() ?? await _db.CheckLimitsIntegrityAsync();
            if (problem != null)
            {
                Logger.Instance.Warn("Limits tamper guard: " + problem);
                await SendTamperAlertAsync("limits_changed", problem, ignoreSwitch: ScreenTime.LimitsSealStore.Read()?.Protected == true);
            }
        }
        catch (Exception ex) { Logger.Instance.Error($"Limits tamper guard failed: {ex.Message}"); }
        finally { Volatile.Write(ref _guarding, 0); }
    }

    public async Task ReloadAndCheckAsync() => await CheckAsync();

    public void ReportLoginLockout(string source) => _ = SendTamperAlertAsync(
        "login_lockout",
        $"Repeated failed dashboard logins from {source} triggered a temporary lockout.");

    /// <summary>Alerts the parent about a remote PC Time change or a wrong remote-PIN guess.</summary>
    /// <param name="security">
    /// True for suspected guessing/lockouts: these are sent even when general tamper alerts are
    /// off, because staying silent while someone guesses the remote PIN would defeat the point.
    /// Ordinary remote changes follow the tamper-alert switch.
    /// </param>
    public void ReportRemoteScreenTime(string message, bool security) =>
        _ = SendTamperAlertAsync(security ? "pc_time_remote_security" : "pc_time_remote", message, ignoreSwitch: security);

    // The SYSTEM service counts remote-PIN lockouts in its status file, including ones triggered by
    // a local process talking to the service directly (which never touches the dashboard).
    private async Task CheckRemotePinLockoutsAsync()
    {
        try
        {
            var path = ScreenTime.ScreenTimeFiles.StatusPath;
            if (!File.Exists(path)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("remote", out var remote) ||
                !remote.TryGetProperty("lockouts", out var lockouts) || !lockouts.TryGetInt32(out var current)) return;

            var seenText = await _db.GetSettingAsync("RemotePinLockoutsSeen", "");
            if (!int.TryParse(seenText, out var seen)) { await _db.SetSettingAsync("RemotePinLockoutsSeen", current.ToString()); return; }
            if (current <= seen) return;

            if (await SendTamperAlertAsync("pc_time_pin_lockout",
                    "Too many wrong remote PC Time PIN attempts locked remote changes. The guesses may have come from the network or from a program on the child PC.",
                    ignoreSwitch: true))
                await _db.SetSettingAsync("RemotePinLockoutsSeen", current.ToString());
        }
        catch (Exception ex) { Logger.Instance.Error($"Remote PIN lockout check failed: {ex.Message}"); }
    }

    public async Task<bool> ReportWatchdogRestartAsync(string details)
    {
        // Every restart is remembered (for the summary email and for spotting repeated closes),
        // even when tamper alerts are off.
        var now = DateTimeOffset.UtcNow;
        var history = RepeatedCloseDetector.Parse(await _db.GetSettingAsync(RestartLogKey, ""));
        history.Add(now);
        await _db.SetSettingAsync(RestartLogKey, RepeatedCloseDetector.Serialize(history, now));

        if ((await _db.GetSettingAsync("TamperAlertsEnabled", "false")) != "true")
            return true;
        var sent = await SendTamperAlertAsync(
            "watchdog_restart",
            $"DeviceMon was stopped and restarted by the GameHost watchdog.\n{details}");

        _ = int.TryParse(await _db.GetSettingAsync("RepeatedCloseThreshold", "3"), out var threshold);
        _ = int.TryParse(await _db.GetSettingAsync("RepeatedCloseWindowMinutes", "30"), out var window);
        if (threshold < 2) threshold = 3;
        if (window < 1) window = 30;
        if (sent && RepeatedCloseDetector.IsFirstAtThreshold(history, now, TimeSpan.FromMinutes(window), threshold))
            await SendTamperAlertAsync("repeated_close",
                $"DeviceMon was closed {threshold} times in the last {window} minutes. Someone is probably closing it on purpose.");
        return sent;
    }

    private const string RestartLogKey = "WatchdogRestartLog";

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
        await CheckRemotePinLockoutsAsync();
        await CheckTimeRequestsAsync();
    }

    // The SYSTEM service records a request when the child answers Yes to "ask for more time".
    // Each one is announced once; approving it happens in the dashboard's PC Time page.
    private async Task CheckTimeRequestsAsync()
    {
        try
        {
            if ((await _db.GetSettingAsync("TimeRequestAlertsEnabled", "true")) != "true") return;
            var pending = ScreenTime.TimeRequestStore.Pending(DateTimeOffset.UtcNow);
            if (pending.Count == 0) return;
            var seen = (await _db.GetSettingAsync("TimeRequestsSeen", "")).Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var request in pending.Where(r => !seen.Contains(r.Id)))
            {
                var message = $"{request.Name} asked for more PC time on {Environment.MachineName}. " +
                              "Open the dashboard, go to PC Time, choose Add 15/30/60 min and save to approve.";
                if (!await SendTamperAlertAsync("pc_time_request", message, ignoreSwitch: true)) continue;
                seen.Add(request.Id);
                await _db.SetSettingAsync("TimeRequestsSeen", string.Join(';', seen.TakeLast(50)));
            }
        }
        catch (Exception ex) { Logger.Instance.Error($"Time request check failed: {ex.Message}"); }
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

        var restarts = RepeatedCloseDetector.CountSince(
            RepeatedCloseDetector.Parse(await _db.GetSettingAsync("WatchdogRestartLog", "")), new DateTimeOffset(from));
        body.AppendLine().AppendLine($"Times DeviceMon was closed and restarted: {restarts}");
        AppendPcTimeToday(body);
        await AppendTopSitesAsync(body, from.Date, due.AddTicks(-1).Date);
        try
        {
            var end = due.AddTicks(-1).Date;
            var trend = WeeklyReport.Build(await _db.GetUsageRangeAsync(end.AddDays(-13), end), end);
            body.AppendLine().Append(WeeklyReport.ToText(trend));
        }
        catch (Exception ex) { Logger.Instance.Warn($"Summary trend section skipped: {ex.Message}"); }

        var error = await _email.SendSystemEmailAsync($"{frequency[..1].ToUpperInvariant() + frequency[1..]} Usage Summary", body.ToString());
        if (error == null)
            await _db.SetSettingAsync("SummaryLastSentDue", due.ToString("O"));
    }

    private async Task AppendTopSitesAsync(StringBuilder body, DateTime from, DateTime to)
    {
        try
        {
            if ((await _db.GetSettingAsync("TrackWebsites", "true")) != "true") return;
            var sites = (await _db.GetSiteUsageRangeAsync(from, to))
                .GroupBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Host: g.Key, Seconds: g.Sum(x => x.Seconds)))
                .OrderByDescending(x => x.Seconds).Take(8).ToList();
            if (sites.Count == 0) return;
            body.AppendLine().AppendLine("Top websites:");
            foreach (var (host, seconds) in sites) body.AppendLine($"{host}: {Format(seconds)}");
        }
        catch { /* the summary is still useful without it */ }
    }

    // PC Time counters belong to the SYSTEM service; its status file shows each account's latest day.
    private static void AppendPcTimeToday(StringBuilder body)
    {
        try
        {
            var path = ScreenTime.ScreenTimeFiles.StatusPath;
            if (!File.Exists(path)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("profiles", out var profiles) || profiles.GetArrayLength() == 0) return;
            body.AppendLine().AppendLine("PC Time (most recent day recorded for each account):");
            foreach (var p in profiles.EnumerateArray())
            {
                var name = p.TryGetProperty("name", out var n) ? n.GetString() : "?";
                var used = p.TryGetProperty("usedSeconds", out var u) ? u.GetInt64() : 0;
                var day = p.TryGetProperty("day", out var d) ? d.GetString() : "";
                var enabled = p.TryGetProperty("enabled", out var e) && e.GetBoolean();
                body.AppendLine($"{name}: {Format(used)} on {day}{(enabled ? "" : " (not controlled)")}");
            }
        }
        catch { /* the summary is still useful without it */ }
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
        if (_activeTamperConditions.Contains(key)) return;
        if (await SendTamperAlertAsync(key, message))
            _activeTamperConditions.Add(key);
    }

    private async Task<bool> SendTamperAlertAsync(string type, string message, bool ignoreSwitch = false)
    {
        if (!ignoreSwitch && (await _db.GetSettingAsync("TamperAlertsEnabled", "false")) != "true") return false;
        Logger.Instance.Warn($"Tamper alert: {message}");
        var emailError = await _email.SendSystemEmailAsync($"Tamper Alert: {type}", message);
        var webhookSent = await _notifications.NotifySystemAsync(type, message);
        if (emailError != null && !webhookSent)
            Logger.Instance.Error($"Tamper alert delivery failed: {emailError}");
        return emailError == null || webhookSent;
    }

    private static string Format(long seconds) => TimeSpan.FromSeconds(seconds) is var value
        ? $"{(int)value.TotalHours}h {value.Minutes}m"
        : "0m";

    public void Dispose() { _timer.Dispose(); _guardTimer.Dispose(); }
}

/// <summary>Spots a child closing DeviceMon again and again, from the watchdog's restart log.</summary>
public static class RepeatedCloseDetector
{
    private const int MaxEntries = 200;

    public static List<DateTimeOffset> Parse(string text)
    {
        var list = new List<DateTimeOffset>();
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (DateTimeOffset.TryParse(part, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var value))
                list.Add(value);
        return list;
    }

    public static string Serialize(List<DateTimeOffset> history, DateTimeOffset now) =>
        string.Join(';', history.Where(h => now - h < TimeSpan.FromDays(35)).OrderBy(h => h)
            .TakeLast(MaxEntries).Select(h => h.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));

    public static int CountWithin(List<DateTimeOffset> history, DateTimeOffset now, TimeSpan window) =>
        history.Count(h => h <= now && now - h <= window);

    /// <summary>True only for the restart that reaches the threshold, so one burst sends one alert.</summary>
    public static bool IsFirstAtThreshold(List<DateTimeOffset> history, DateTimeOffset now, TimeSpan window, int threshold) =>
        CountWithin(history, now, window) == threshold;

    public static int CountSince(List<DateTimeOffset> history, DateTimeOffset since) => history.Count(h => h >= since);
}
