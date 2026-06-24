using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MonitorAndControl.Data;
using MonitorAndControl.Models;
using System.Collections.Concurrent;

namespace MonitorAndControl.Services;

public class EmailService : IDisposable
{
    private readonly UsageDatabase _db;
    private readonly WindowTracker _tracker;
    private readonly LimitEnforcer _enforcer;
    private readonly SchedulerService _scheduler;
    private readonly System.Threading.Timer _pollTimer;
    private readonly System.Threading.Timer _startAlertTimer;
    private readonly ConcurrentDictionary<string, byte> _startAlertsSentToday = new(StringComparer.OrdinalIgnoreCase);
    private int _pollingInbox;
    private int _pollingStarts;
    private readonly object _startAlertDateSync = new();
    private readonly DateTimeOffset _commandPollingStartedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _startAlertsSuppressedUntil;
    private bool _breachNotifyEnabled;
    private bool _killNotifyEnabled;
    private bool _startNotifyEnabled;
    private bool _controlEnabled;
    public bool IsEnabled => _breachNotifyEnabled || _killNotifyEnabled || _startNotifyEnabled || _controlEnabled;

    private string _email = "";
    private string _password = "";
    private string _allowedSender = "";
    private string _deviceId = Environment.MachineName;
    private string _startAlertDate = DateTime.Now.ToString("yyyy-MM-dd");
    private const string SmtpHost = "smtp.gmail.com";
    private const int SmtpPort = 587;
    private const string ImapHost = "imap.gmail.com";
    private const int ImapPort = 993;

    public EmailService(UsageDatabase db, WindowTracker tracker, LimitEnforcer enforcer, SchedulerService scheduler)
    {
        _db = db;
        _tracker = tracker;
        _enforcer = enforcer;
        _scheduler = scheduler;
        _pollTimer = new System.Threading.Timer(PollInbox, null, Timeout.Infinite, Timeout.Infinite);
        _startAlertTimer = new System.Threading.Timer(PollRunningTrackedApps, null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task LoadSettingsAsync()
    {
        _email = await _db.GetSettingAsync("EmailAddress", "");
        var storedPassword = await _db.GetSettingAsync("EmailPassword", "");
        _password = SecretProtector.Unprotect(storedPassword);
        if (!string.IsNullOrEmpty(storedPassword) && !storedPassword.StartsWith("dpapi:", StringComparison.Ordinal))
            await _db.SetSettingAsync("EmailPassword", SecretProtector.Protect(_password));
        _allowedSender = await _db.GetSettingAsync("EmailAllowedSender", _email);
        _deviceId = NormalizeDeviceId(await _db.GetSettingAsync("EmailDeviceId", Environment.MachineName));
        if (string.IsNullOrWhiteSpace(_deviceId))
            _deviceId = NormalizeDeviceId(Environment.MachineName);
        var legacyNotify = await _db.GetSettingAsync("EmailNotifyEnabled", "false");
        _breachNotifyEnabled = !string.IsNullOrEmpty(_email) && !string.IsNullOrEmpty(_password)
            && (await _db.GetSettingAsync("EmailBreachNotifyEnabled", legacyNotify)) == "true";
        _killNotifyEnabled = !string.IsNullOrEmpty(_email) && !string.IsNullOrEmpty(_password)
            && (await _db.GetSettingAsync("EmailKillNotifyEnabled", legacyNotify)) == "true";
        _startNotifyEnabled = !string.IsNullOrEmpty(_email) && !string.IsNullOrEmpty(_password)
            && (await _db.GetSettingAsync("EmailStartNotifyEnabled", "false")) == "true";
        _controlEnabled = !string.IsNullOrEmpty(_email) && !string.IsNullOrEmpty(_password)
            && (await _db.GetSettingAsync("EmailControlEnabled", "false")) == "true";

        if (_startNotifyEnabled)
            _startAlertTimer.Change(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
        else
            _startAlertTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public void StartPolling()
    {
        if (_controlEnabled && !string.IsNullOrEmpty(_email))
            _pollTimer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
    }

    public void StopPolling() => _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);

    private async void PollRunningTrackedApps(object? state)
    {
        if (!_startNotifyEnabled || string.IsNullOrEmpty(_email)) return;
        if (IsStartAlertSuppressed()) return;
        if (Interlocked.Exchange(ref _pollingStarts, 1) != 0) return;

        try
        {
            var runningProcessNames = _tracker.GetRunningProcessNames();
            foreach (var processName in runningProcessNames)
            {
                var appName = _tracker.KnownApps.TryGetValue(processName, out var friendly)
                    ? friendly
                    : Path.GetFileNameWithoutExtension(processName);
                await NotifyTrackedAppStartedAsync(appName, processName);
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"App-start email polling failed: {ex.Message}");
        }
        finally { Volatile.Write(ref _pollingStarts, 0); }
    }

    public async Task<string?> SendBreachAlertAsync(string subject, string body)
    {
        if (!_breachNotifyEnabled || string.IsNullOrEmpty(_email)) return "Breach email alerts not configured";
        return await SendMailAsync(subject, body, _email);
    }

    public async Task<string?> SendKillAlertAsync(string subject, string body)
    {
        if (!_killNotifyEnabled || string.IsNullOrEmpty(_email)) return "App-closed email alerts not configured";
        return await SendMailAsync(subject, body, _email);
    }

    public void SuppressStartAlertsFor(TimeSpan duration)
    {
        lock (_startAlertDateSync)
        {
            var until = DateTimeOffset.Now.Add(duration);
            if (until > _startAlertsSuppressedUntil) _startAlertsSuppressedUntil = until;
        }
    }

    private bool IsStartAlertSuppressed()
    {
        lock (_startAlertDateSync)
            return _startAlertsSuppressedUntil > DateTimeOffset.Now;
    }

    private async Task<string?> SendMailAsync(string subject, string body, string to)
    {
        try
        {
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress("Monitor", _email));
            msg.To.Add(new MailboxAddress("Parent", to));
            msg.Subject = $"[{_deviceId}] {subject}";
            msg.Body = new TextPart("plain") { Text = WithDeviceHeader(body) };

            using var client = new SmtpClient();
            await client.ConnectAsync(SmtpHost, SmtpPort, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_email, _password);
            await client.SendAsync(msg);
            await client.DisconnectAsync(true);
            Logger.Instance.Info($"Email sent: {subject}");
            return null; // success
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Email send failed: {subject} - {ex.Message}");
            return ex.Message;
        }
    }

    public async Task<string?> NotifyTrackedAppStartedAsync(string appName, string processName)
    {
        if (!_startNotifyEnabled || string.IsNullOrEmpty(_email))
            return "Email start alerts not configured";
        if (IsStartAlertSuppressed())
            return "App-start alerts temporarily suppressed after watchdog recovery";

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        lock (_startAlertDateSync)
        {
            if (_startAlertDate != today)
            {
                _startAlertsSentToday.Clear();
                _startAlertDate = today;
            }
        }

        if (!_startAlertsSentToday.TryAdd(appName, 0))
            return null;

        var limits = await _db.GetLimitRulesAsync();
        var groups = await _db.GetLimitGroupsAsync();
        if (!limits.Any(l => l.Enabled && l.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase)) &&
            !groups.Any(g => g.Enabled && g.AppNames.Contains(appName, StringComparer.OrdinalIgnoreCase)))
        {
            _startAlertsSentToday.TryRemove(appName, out _);
            return null;
        }

        var subject = $"App Started: {appName}";
        var body = $"{appName} was opened.\nProcess: {processName}\nTime: {DateTime.Now:g}";
        return await SendMailAsync(subject, body, _email);
    }

    public async Task<string?> TestStartEmailAsync()
    {
        if (string.IsNullOrEmpty(_email)) return "Email address not set";
        if (string.IsNullOrEmpty(_password)) return "App password not set";

        return await SendMailAsync(
            "App Started: Test",
            $"This is a test app-start email at {DateTime.Now:g}.",
            _email);
    }

    public void ResetStartEmailMarkers()
    {
        lock (_startAlertDateSync)
        {
            _startAlertsSentToday.Clear();
            _startAlertDate = DateTime.Now.ToString("yyyy-MM-dd");
        }
    }

    private async void PollInbox(object? state)
    {
        if (!_controlEnabled || string.IsNullOrEmpty(_email)) return;
        if (Interlocked.Exchange(ref _pollingInbox, 1) != 0) return;
        try
        {
            using var client = new ImapClient();
            await client.ConnectAsync(ImapHost, ImapPort, SecureSocketOptions.SslOnConnect);
            await client.AuthenticateAsync(_email, _password);
            await client.Inbox.OpenAsync(MailKit.FolderAccess.ReadWrite);

            var allowedSenders = GetAllowedSenders();
            MailKit.Search.SearchQuery fromQuery = MailKit.Search.SearchQuery.FromContains(allowedSenders[0]);
            foreach (var sender in allowedSenders.Skip(1))
                fromQuery = fromQuery.Or(MailKit.Search.SearchQuery.FromContains(sender));
            // Gmail's Seen flag is shared by every computer using this mailbox. It cannot
            // be used as command-delivery state because the first computer would hide a
            // broadcast from all the others. Each installation keeps its own receipt list.
            var allUids = await client.Inbox.SearchAsync(fromQuery);
            var uids = allUids.Skip(Math.Max(0, allUids.Count - 250)).ToList();
            if (uids.Count == 0) { await client.DisconnectAsync(true); return; }

            var items = await client.Inbox.FetchAsync(uids, new MailKit.FetchRequest(MailKit.MessageSummaryItems.Full));
            var initialized = (await _db.GetSettingAsync("EmailCommandTrackingInitialized", "false")) == "true";
            foreach (var item in items)
            {
                var mime = await client.Inbox.GetMessageAsync(item.UniqueId);
                var from = mime.From.Mailboxes.FirstOrDefault()?.Address ?? "";
                var subject = mime.Subject ?? "";
                var body = (mime.TextBody ?? mime.HtmlBody ?? "").Trim();
                if (!allowedSenders.Contains(from, StringComparer.OrdinalIgnoreCase))
                    continue;

                var commandText = ExtractCommandText(subject, body);
                if (commandText == null)
                    continue;

                var messageKey = !string.IsNullOrWhiteSpace(mime.MessageId)
                    ? $"message-id:{mime.MessageId.Trim()}"
                    : $"imap:{client.Inbox.UidValidity}:{item.UniqueId.Id}";
                if (await _db.IsEmailCommandProcessedAsync(messageKey))
                    continue;

                var receivedAt = item.InternalDate ?? mime.Date;
                var tooOld = receivedAt < DateTimeOffset.UtcNow.AddDays(-30);
                var predatesUpgrade = !initialized
                    && item.Flags.HasValue
                    && item.Flags.Value.HasFlag(MailKit.MessageFlags.Seen)
                    && receivedAt < _commandPollingStartedAt.AddMinutes(-2);
                if (tooOld || predatesUpgrade)
                {
                    await _db.MarkEmailCommandProcessedAsync(messageKey);
                    continue;
                }

                if (!IsCommandForThisDevice(commandText, out var effectiveCommand))
                {
                    Logger.Instance.Info($"Email command ignored on {_deviceId}; target does not match: \"{commandText}\"");
                    await _db.MarkEmailCommandProcessedAsync(messageKey);
                    continue;
                }

                Logger.Instance.Info($"Email command from {from}: \"{effectiveCommand}\"");
                var reply = await ProcessCommandAsync(effectiveCommand);
                if (reply != null)
                {
                    Logger.Instance.Info($"Email reply to {from}: \"{reply}\"");
                    await SendReplyAsync(from, subject, reply);
                }

                // Mark locally after command execution. This prevents duplicate side effects
                // even when sending the reply fails and allows every PC to handle broadcasts.
                await _db.MarkEmailCommandProcessedAsync(messageKey);
                client.Inbox.SetFlags(item.UniqueId, MailKit.MessageFlags.Seen, true);
            }

            if (!initialized)
                await _db.SetSettingAsync("EmailCommandTrackingInitialized", "true");
            await _db.DeleteProcessedEmailCommandsBeforeAsync(DateTimeOffset.UtcNow.AddDays(-90));

            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Email command polling failed: {ex.Message}");
        }
        finally { Volatile.Write(ref _pollingInbox, 0); }
    }

    private string[] GetAllowedSenders()
    {
        var raw = string.IsNullOrWhiteSpace(_allowedSender) ? _email : _allowedSender;
        return raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => MimeKit.MailboxAddress.TryParse(s, out _))
            .DefaultIfEmpty(_email)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ExtractCommandText(string subject, string body)
    {
        foreach (var candidate in new[] { subject, body })
        {
            var lines = candidate.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith("mc:", StringComparison.OrdinalIgnoreCase))
                    return line[3..].Trim();
                if (line.StartsWith("monitor:", StringComparison.OrdinalIgnoreCase))
                    return line[8..].Trim();
            }
        }
        return null;
    }

    public async Task<string?> SendSystemEmailAsync(string subject, string body)
    {
        if (string.IsNullOrEmpty(_email) || string.IsNullOrEmpty(_password))
            return "Email credentials not configured";
        return await SendMailAsync(subject, body, _email);
    }

    private bool IsCommandForThisDevice(string commandText, out string effectiveCommand)
    {
        effectiveCommand = commandText.Trim();
        var match = Regex.Match(effectiveCommand, @"^@(?<id>[A-Za-z0-9_.-]+)\s+(?<cmd>.+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            match = Regex.Match(effectiveCommand, @"^to\s+(?<id>[A-Za-z0-9_.-]+)\s+(?<cmd>.+)$", RegexOptions.IgnoreCase);

        if (!match.Success)
            return true;

        var target = NormalizeDeviceId(match.Groups["id"].Value);
        if (!target.Equals(_deviceId, StringComparison.OrdinalIgnoreCase))
            return false;

        effectiveCommand = match.Groups["cmd"].Value.Trim();
        return effectiveCommand.Length > 0;
    }

    private async Task<string?> ProcessCommandAsync(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();

            // help
            if (lower.StartsWith("help"))
                return @"Commands:
  @device status                - run command only on one PC
  status                        - current limits, schedule, today's usage
  set [app] [minutes] min       - set daily limit (e.g. set aces 60 min)
  bonus [app] [minutes] min     - add bonus time today (e.g. bonus aces 15 min)
  extend [app] until bedtime    - add enough time for the current allowed window
  set schedule [day] [start]-[end] - set schedule (e.g. set schedule weekday 22:00-06:00)
  set kill-delay [seconds]      - set kill delay
  add [process.exe] [appname]   - register a known app (e.g. add aces.exe Aces)

Prefix commands with mc:, for example: mc: status or mc: @" + _deviceId + " status";

            // status
            if (lower.StartsWith("status"))
            {
                var limits = await _db.GetLimitRulesAsync();
                var usage = await _db.GetTodayUsageAsync();
                var schedule = await _db.GetScheduleRulesAsync();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Device: {_deviceId}");
                sb.AppendLine($"Computer: {Environment.MachineName}");
                sb.AppendLine($"Current app: {_tracker.CurrentAppName ?? "None"} ({_tracker.CurrentProcessName ?? "idle"})");
                sb.AppendLine();
                sb.AppendLine("=== Top Apps Today ===");
                if (usage.Count == 0)
                {
                    sb.AppendLine("  No activity yet today.");
                }
                else
                {
                    foreach (var u in usage.Take(8))
                    {
                        var processText = string.IsNullOrWhiteSpace(u.ProcessName) ? "" : $" ({u.ProcessName})";
                        sb.AppendLine($"  {u.AppName}{processText}: {u.DurationFormatted}");
                    }
                }
                sb.AppendLine();
                sb.AppendLine("=== Limits ===");
                foreach (var l in limits)
                {
                    var u = usage.FirstOrDefault(x => x.AppName.Equals(l.AppName, StringComparison.OrdinalIgnoreCase));
                    var bonus = await _db.GetTodayBonusMinutesAsync(l.AppName);
                    var bonusText = bonus > 0 ? $", bonus: +{bonus} min" : "";
                    sb.AppendLine($"  {l.AppName}: {l.DailyMaxMinutes} min/day{bonusText} (used: {u?.DurationFormatted ?? "0m"})");
                }
                sb.AppendLine("\n=== Schedule ===");
                foreach (var r in schedule)
                {
                    var target = string.IsNullOrWhiteSpace(r.AppName) ? "All apps" : r.AppName;
                    sb.AppendLine($"  {target}: {r.DayOfWeek} {r.StartTime}-{r.EndTime} ({(r.Enabled ? "on" : "off")})");
                }
                var delay = await _db.GetKillDelayAsync();
                sb.AppendLine($"\nKill delay: {delay}s");
                var legacyAlerts = await _db.GetSettingAsync("EmailNotifyEnabled", "false");
                var breachAlerts = await _db.GetSettingAsync("EmailBreachNotifyEnabled", legacyAlerts);
                var killAlerts = await _db.GetSettingAsync("EmailKillNotifyEnabled", legacyAlerts);
                sb.AppendLine($"Limit-reached emails: {(breachAlerts == "true" ? "on" : "off")}");
                sb.AppendLine($"App-closed emails: {(killAlerts == "true" ? "on" : "off")}");
                return sb.ToString();
            }

            // set [app] [minutes] min
            var setMatch = Regex.Match(line, @"^set\s+(.+?)\s+(\d+)\s*min", RegexOptions.IgnoreCase);
            if (setMatch.Success)
            {
                var appName = setMatch.Groups[1].Value.Trim();
                var minutes = int.Parse(setMatch.Groups[2].Value);
                await _db.SaveLimitRuleAsync(new AppLimitRule
                {
                    AppName = appName,
                    DailyMaxMinutes = minutes,
                    Enabled = true
                });
                _enforcer.ClearExceeded(appName);
                return $"OK: Limit set: {appName} = {minutes} min/day";
            }

            // bonus [app] [minutes] min
            var bonusMatch = Regex.Match(line, @"^(?:bonus|extend)\s+(.+?)\s+(\d+)\s*min", RegexOptions.IgnoreCase);
            if (bonusMatch.Success)
            {
                var appName = bonusMatch.Groups[1].Value.Trim();
                var minutes = int.Parse(bonusMatch.Groups[2].Value);
                if (minutes is < 1 or > 240)
                    return "Error: Bonus minutes must be between 1 and 240.";

                var limits = await _db.GetLimitRulesAsync();
                if (!limits.Any(l => l.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase)))
                    return $"Error: No limit found for {appName}.";

                var total = await _db.AddTodayBonusMinutesAsync(appName, minutes);
                _enforcer.ClearExceeded(appName);
                return $"OK: Bonus time added: {appName} +{minutes} min today ({total} min total).";
            }

            var bedtimeMatch = Regex.Match(line, @"^(?:bonus|extend|allow)\s+(.+?)\s+(?:until\s+)?bedtime$", RegexOptions.IgnoreCase);
            if (bedtimeMatch.Success)
            {
                var appName = bedtimeMatch.Groups[1].Value.Trim();
                var limits = await _db.GetLimitRulesAsync();
                var limit = limits.FirstOrDefault(l => l.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));
                if (limit == null)
                    return $"Error: No limit found for {appName}.";

                var now = DateTime.Now;
                var rules = await _scheduler.GetRulesAsync();
                var allowedUntil = SchedulerService.GetCurrentAllowedWindowEnd(rules, appName, now)
                    ?? now.Date.AddDays(1);
                if (allowedUntil <= now)
                    return "Error: No remaining allowed time today.";

                var usageSeconds = await _db.GetAppTodaySecondsAsync(appName);
                var currentBonus = await _db.GetTodayBonusMinutesAsync(appName);
                var currentAllowanceMinutes = limit.DailyMaxMinutes + currentBonus;
                var desiredAllowanceMinutes = (int)Math.Ceiling((usageSeconds + (allowedUntil - now).TotalSeconds) / 60.0);
                var minutesToAdd = Math.Clamp(desiredAllowanceMinutes - currentAllowanceMinutes, 1, 720);
                var total = await _db.AddTodayBonusMinutesAsync(appName, minutesToAdd);
                _enforcer.ClearExceeded(appName);
                return $"OK: Bonus time added until bedtime: {appName} +{minutesToAdd} min today ({total} min total, until {allowedUntil:g}).";
            }

            // set schedule [day] [start]-[end]
            var schedMatch = Regex.Match(line, @"^set\s+schedule\s+(\w+)\s+(\d{1,2}:\d{2})-(\d{1,2}:\d{2})", RegexOptions.IgnoreCase);
            if (schedMatch.Success)
            {
                var day = schedMatch.Groups[1].Value;
                day = char.ToUpper(day[0]) + day.Substring(1).ToLower();
                if (day is "Weekday" or "Weekend" or "Everyday" or "Monday" or "Tuesday" or "Wednesday" or "Thursday" or "Friday" or "Saturday" or "Sunday")
                {
                    await _db.SaveScheduleRuleAsync(new ScheduleRule
                    {
                        DayOfWeek = day,
                        StartTime = schedMatch.Groups[2].Value,
                        EndTime = schedMatch.Groups[3].Value,
                        Enabled = true
                    });
                    _scheduler.InvalidateCache();
                    return $"OK: Schedule added: {day} {schedMatch.Groups[2].Value}-{schedMatch.Groups[3].Value}";
                }
                return $"Error: Invalid day: {day}. Use: Weekday, Weekend, Everyday, or day name.";
            }

            // set kill-delay [seconds]
            var delayMatch = Regex.Match(line, @"^set\s+kill[\s-]?delay\s+(\d+)", RegexOptions.IgnoreCase);
            if (delayMatch.Success)
            {
                var secs = int.Parse(delayMatch.Groups[1].Value);
                await _db.SetKillDelayAsync(secs);
                return $"OK: Kill delay set to {secs}s";
            }

            // add [process.exe] [appName]
            var addMatch = Regex.Match(line, @"^add\s+(\S+)\s+(.+)", RegexOptions.IgnoreCase);
            if (addMatch.Success)
            {
                var proc = addMatch.Groups[1].Value;
                var name = addMatch.Groups[2].Value.Trim();
                var existing = (await _db.GetAppMappingsAsync()).FirstOrDefault(mapping =>
                    mapping.ProcessName.Equals(proc, StringComparison.OrdinalIgnoreCase));
                var countInBackground = existing?.CountInBackground ?? false;
                var ignoreOverlayFocus = existing?.IgnoreOverlayFocus ?? false;
                await _db.SaveAppMappingAsync(proc, name, countInBackground, ignoreOverlayFocus);
                _tracker.AddKnownApp(proc, name, countInBackground, ignoreOverlayFocus);
                return $"OK: App added: {proc} -> {name}";
            }
        }
        return null;
    }

    private async Task SendReplyAsync(string to, string subject, string body)
    {
        try
        {
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress("Monitor", _email));
            msg.To.Add(new MailboxAddress("", to));
            msg.Subject = $"Re: [{_deviceId}] " + subject;
            msg.Body = new TextPart("plain") { Text = WithDeviceHeader(body) };

            using var client = new SmtpClient();
            await client.ConnectAsync(SmtpHost, SmtpPort, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_email, _password);
            await client.SendAsync(msg);
            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Email reply failed to {to}: {ex.Message}");
        }
    }

    public async Task<string?> TestEmailAsync(string? emailOverride = null, string? passwordOverride = null)
    {
        // Test always tries to send regardless of _enabled (checkbox)
        var email = string.IsNullOrWhiteSpace(emailOverride) ? _email : emailOverride.Trim();
        var password = string.IsNullOrWhiteSpace(passwordOverride) ? _password : passwordOverride.Replace(" ", "");
        if (string.IsNullOrEmpty(email)) return "Email address not set";
        if (string.IsNullOrEmpty(password)) return "App password not set";
        try
        {
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress("Monitor", email));
            msg.To.Add(new MailboxAddress("Parent", email));
            msg.Subject = $"[{_deviceId}] Test from Monitor";
            msg.Body = new TextPart("plain") { Text = WithDeviceHeader("Email notification test - If you receive this, email is configured correctly.") };

            using var client = new SmtpClient();
            await client.ConnectAsync(SmtpHost, SmtpPort, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(email, password);
            await client.SendAsync(msg);
            await client.DisconnectAsync(true);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public void Dispose()
    {
        StopPolling();
        _startAlertTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _pollTimer?.Dispose();
        _startAlertTimer?.Dispose();
    }

    private string WithDeviceHeader(string body) =>
        $"Device: {_deviceId}\nComputer: {Environment.MachineName}\n\n{body}";

    public static string NormalizeDeviceId(string value)
    {
        var normalized = Regex.Replace((value ?? "").Trim(), @"[^A-Za-z0-9_.-]+", "-").Trim('-');
        return normalized.Length > 40 ? normalized[..40] : normalized;
    }
}
