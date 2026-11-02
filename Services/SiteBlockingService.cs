using MonitorAndControl.Data;
using MonitorAndControl.ScreenTime;

namespace MonitorAndControl.Services;

/// <summary>
/// Every 30 seconds works out which websites are blocked right now (blocked outright, or today's daily
/// limit used up) and tells the SYSTEM service, which writes them into the browsers' policy.
/// </summary>
public sealed class SiteBlockingService : IDisposable
{
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);

    private readonly UsageDatabase _db;
    private readonly System.Threading.Timer _timer;
    private string? _lastSent;
    private DateTime _lastSentAt;
    private bool _serviceWarned;
    private int _running;

    public SiteBlockingService(UsageDatabase db)
    {
        _db = db;
        _timer = new System.Threading.Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));

    private async void Tick(object? state)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            var rules = await _db.GetSiteLimitsAsync();
            var usage = rules.Count == 0
                ? new List<Models.SiteUsageRecord>()
                : await _db.GetSiteUsageRangeAsync(MonitorAndControl.ScreenTime.ParentClock.Today, MonitorAndControl.ScreenTime.ParentClock.Today);
            var status = SiteLimitLogic.Evaluate(rules, usage);
            var blocked = SiteLimitLogic.BlockedHosts(status).OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
            var allowOnly = await _db.GetSettingAsync("SiteAllowOnly", "false") == "true";
            var safe = await _db.GetSettingAsync("SafeBrowsing", "false") == "true";
            var homework = HomeworkState.IsActiveNow();
            // Always sent (the service only uses it in allow-list or homework mode), so a new session starts with it already there.
            var allowed = SiteLimitLogic.AllowedHosts(status).OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();

            // Rules for this Windows account, counted from its own browsing. The service puts them in the
            // browsers while this account is the one at the screen.
            var sid = AccountIdentity.CurrentSid;
            var ownRules = sid.Length == 0 ? new List<Models.SiteLimit>() : await _db.GetUserSiteLimitsAsync(sid);
            var ownStatus = ownRules.Count == 0 ? new List<SiteLimitStatus>()
                : SiteLimitLogic.Evaluate(ownRules, await _db.GetUserSiteUsageTodayAsync(sid));
            var ownBlocked = SiteLimitLogic.BlockedHosts(ownStatus).OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
            var ownAllowed = SiteLimitLogic.AllowedHosts(ownStatus).OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();

            var key = string.Join('|', blocked) + "#" + string.Join('|', allowed) + $"#{allowOnly}#{safe}#{homework}#" +
                      string.Join('|', ownBlocked) + "#" + string.Join('|', ownAllowed);
            if (key == _lastSent && DateTime.UtcNow - _lastSentAt < Refresh) return;

            var reply = await ScreenTimeClient.SendAsync(new ScreenTimeRequest
            {
                Command = "set-blocked-sites", BlockedHosts = blocked, AllowedHosts = allowed, AllowOnly = allowOnly, SafeBrowsing = safe,
                AccountBlockedHosts = ownBlocked, AccountAllowedHosts = ownAllowed
            }, CancellationToken.None);
            if (reply.Ok)
            {
                if (key != _lastSent) Logger.Instance.Info($"Browser rules sent: {blocked.Count} blocked, allow-list mode {(allowOnly ? "on (" + allowed.Count + " allowed)" : "off")}, safe browsing {(safe ? "on" : "off")}, {ownBlocked.Count} blocked for this account only.");
                _lastSent = key;
                _lastSentAt = DateTime.UtcNow;
                _serviceWarned = false;
            }
            else Logger.Instance.Warn("The service did not accept the website block list: " + reply.Error);
        }
        catch (Exception ex)
        {
            // Without the GameHost service there is nothing that can write browser policy.
            if (!_serviceWarned) Logger.Instance.Warn("Website blocking is waiting for the GameHost service: " + ex.Message);
            _serviceWarned = true;
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    public void Dispose() => _timer.Dispose();
}
