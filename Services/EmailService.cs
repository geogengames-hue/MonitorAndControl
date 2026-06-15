using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MonitorAndControl.Data;
using MonitorAndControl.Models;

namespace MonitorAndControl.Services;

public class EmailService : IDisposable
{
    private readonly UsageDatabase _db;
    private readonly WindowTracker _tracker;
    private readonly LimitEnforcer _enforcer;
    private readonly SchedulerService _scheduler;
    private readonly System.Threading.Timer _pollTimer;
    private readonly System.Threading.Timer _startAlertTimer;
    private readonly HashSet<string> _startAlertsSentToday = new(StringComparer.OrdinalIgnoreCase);
    private bool _notifyEnabled;
    private bool _startNotifyEnabled;
    private bool _controlEnabled;
    public bool IsEnabled => _notifyEnabled || _controlEnabled;

    private string _email = "";
    private string _password = "";
    private string _allowedSender = "";
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
        _notifyEnabled = !string.IsNullOrEmpty(_email) && !string.IsNullOrEmpty(_password)
            && (await _db.GetSettingAsync("EmailNotifyEnabled", "false")) == "true";
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
        catch { }
    }

    public async Task<string?> SendAlertAsync(string subject, string body)
    {
        if (!_notifyEnabled || string.IsNullOrEmpty(_email)) return "Email alerts not configured";
        return await SendMailAsync(subject, body, _email);
    }

    private async Task<string?> SendMailAsync(string subject, string body, string to)
    {
        try
        {
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress("Monitor", _email));
            msg.To.Add(new MailboxAddress("Parent", to));
            msg.Subject = subject;
            msg.Body = new TextPart("plain") { Text = body };

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
            Logger.Instance.Error($"Email send failed: {subject} — {ex.Message}");
            return ex.Message;
        }
    }

    public async Task<string?> NotifyTrackedAppStartedAsync(string appName, string processName)
    {
        if (!_startNotifyEnabled || string.IsNullOrEmpty(_email))
            return "Email start alerts not configured";

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_startAlertDate != today)
        {
            _startAlertsSentToday.Clear();
            _startAlertDate = today;
        }

        if (!_startAlertsSentToday.Add(appName))
            return null;

        var limits = await _db.GetLimitRulesAsync();
        if (!limits.Any(l => l.Enabled && l.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase)))
        {
            _startAlertsSentToday.Remove(appName);
            return null;
        }

        var subject = $"App Started: {appName}";
        var body = $"{appName} was opened on {Environment.MachineName}.\nProcess: {processName}\nTime: {DateTime.Now:g}";
        return await SendMailAsync(subject, body, _email);
    }

    public async Task<string?> TestStartEmailAsync()
    {
        if (string.IsNullOrEmpty(_email)) return "Email address not set";
        if (string.IsNullOrEmpty(_password)) return "App password not set";

        return await SendMailAsync(
            "App Started: Test",
            $"This is a test app-start email from {Environment.MachineName} at {DateTime.Now:g}.",
            _email);
    }

    public void ResetStartEmailMarkers()
    {
        _startAlertsSentToday.Clear();
        _startAlertDate = DateTime.Now.ToString("yyyy-MM-dd");
    }

    private async void PollInbox(object? state)
    {
        if (!_controlEnabled || string.IsNullOrEmpty(_email)) return;
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
            var uids = await client.Inbox.SearchAsync(MailKit.Search.SearchQuery.NotSeen.And(fromQuery));
            if (uids.Count == 0) { await client.DisconnectAsync(true); return; }

            var items = await client.Inbox.FetchAsync(uids, new MailKit.FetchRequest(MailKit.MessageSummaryItems.Full));
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

                Logger.Instance.Info($"Email command from {from}: \"{commandText}\"");
                var reply = await ProcessCommandAsync(commandText);
                if (reply != null)
                {
                    Logger.Instance.Info($"Email reply to {from}: \"{reply}\"");
                    await SendReplyAsync(from, subject, reply);
                }

                client.Inbox.SetFlags(item.UniqueId, MailKit.MessageFlags.Seen, true);
            }

            await client.DisconnectAsync(true);
        }
        catch { }
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

    private async Task<string?> ProcessCommandAsync(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();

            // help
            if (lower.StartsWith("help"))
                return @"Commands:
  status                        - current limits, schedule, today's usage
  set [app] [minutes] min       - set daily limit (e.g. set aces 60 min)
  set schedule [day] [start]-[end] - set schedule (e.g. set schedule weekday 22:00-06:00)
  set kill-delay [seconds]      - set kill delay
  add [process.exe] [appname]   - register a known app (e.g. add aces.exe Aces)

Prefix commands with mc:, for example: mc: status";

            // status
            if (lower.StartsWith("status"))
            {
                var limits = await _db.GetLimitRulesAsync();
                var usage = await _db.GetTodayUsageAsync();
                var schedule = await _db.GetScheduleRulesAsync();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=== Limits ===");
                foreach (var l in limits)
                {
                    var u = usage.FirstOrDefault(x => x.AppName.Equals(l.AppName, StringComparison.OrdinalIgnoreCase));
                    sb.AppendLine($"  {l.AppName}: {l.DailyMaxMinutes} min/day (used: {u?.DurationFormatted ?? "0m"})");
                }
                sb.AppendLine("\n=== Schedule ===");
                foreach (var r in schedule)
                    sb.AppendLine($"  {r.DayOfWeek}: {r.StartTime}-{r.EndTime} ({(r.Enabled ? "on" : "off")})");
                var delay = await _db.GetKillDelayAsync();
                sb.AppendLine($"\nKill delay: {delay}s");
                var tracking = await _db.GetSettingAsync("EmailNotifyEnabled", "false");
                sb.AppendLine($"Email alerts: {(tracking == "true" ? "on" : "off")}");
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
                return $"✅ Limit set: {appName} = {minutes} min/day";
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
                    return $"✅ Schedule added: {day} {schedMatch.Groups[2].Value}-{schedMatch.Groups[3].Value}";
                }
                return $"❌ Invalid day: {day}. Use: Weekday, Weekend, Everyday, or day name.";
            }

            // set kill-delay [seconds]
            var delayMatch = Regex.Match(line, @"^set\s+kill[\s-]?delay\s+(\d+)", RegexOptions.IgnoreCase);
            if (delayMatch.Success)
            {
                var secs = int.Parse(delayMatch.Groups[1].Value);
                await _db.SetKillDelayAsync(secs);
                return $"✅ Kill delay set to {secs}s";
            }

            // add [process.exe] [appName]
            var addMatch = Regex.Match(line, @"^add\s+(\S+)\s+(.+)", RegexOptions.IgnoreCase);
            if (addMatch.Success)
            {
                var proc = addMatch.Groups[1].Value;
                var name = addMatch.Groups[2].Value.Trim();
                await _db.SaveAppMappingAsync(proc, name);
                _tracker.AddKnownApp(proc, name);
                return $"✅ App added: {proc} → {name}";
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
            msg.Subject = "Re: " + subject;
            msg.Body = new TextPart("plain") { Text = body };

            using var client = new SmtpClient();
            await client.ConnectAsync(SmtpHost, SmtpPort, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_email, _password);
            await client.SendAsync(msg);
            await client.DisconnectAsync(true);
        }
        catch { }
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
            msg.Subject = "Test from Monitor";
            msg.Body = new TextPart("plain") { Text = "Email notification test — If you receive this, email is configured correctly." };

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
}
