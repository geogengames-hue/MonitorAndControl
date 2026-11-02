using System.Collections.Concurrent;
using MonitorAndControl.Data;
using MonitorAndControl.Models;
using MonitorAndControl.ScreenTime;

namespace MonitorAndControl.Services;

/// <summary>
/// "Ask to open this site": browsers show their own page for a blocked site, so DeviceMon notices when the
/// child is on one and offers to ask the parent. The parent answers from the alert or the Limits page;
/// the child is told the answer. A request grants nothing by itself.
/// </summary>
public sealed class SiteRequestService : IDisposable
{
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(30);

    private readonly UsageDatabase _db;
    private readonly SiteBlockingService _blocking;
    private readonly System.Threading.Timer _timer;
    private readonly ConcurrentDictionary<string, DateTime> _asked = new(StringComparer.OrdinalIgnoreCase);
    private int _asking;

    public SiteRequestService(UsageDatabase db, WebsiteTracker websites, SiteBlockingService blocking)
    {
        _db = db;
        _blocking = blocking;
        websites.SiteSeen += host => _ = OnSiteSeenAsync(host);
        _timer = new System.Threading.Timer(_ => _ = TellAnswersAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private async Task OnSiteSeenAsync(string seen)
    {
        try
        {
            if (AccountIdentity.IsAdministrator || !SiteBlocking.TryNormalizeHost(seen, out var host)) return;
            if (_blocking.CurrentPlan is not { } plan || !SiteBlocking.IsBlocked(plan, host)) return;
            if (_asked.TryGetValue(host, out var last) && DateTime.UtcNow - last < AskAgainAfter) return;
            if (await _db.GetSettingAsync("SiteRequestsEnabled", "true") != "true") return;
            var sid = AccountIdentity.CurrentSid;
            if (sid.Length == 0 || (await _db.GetSiteRequestsAsync("waiting", sid)).Any(r => r.Host == host)) return;
            // One question at a time; the browser keeps showing the blocked page every few seconds.
            if (Interlocked.Exchange(ref _asking, 1) != 0) return;
            _asked[host] = DateTime.UtcNow;
            ChildNotice.Ask($"{host} is blocked on this PC. Ask your parent to allow it?", yesNo: true, yes =>
            {
                Volatile.Write(ref _asking, 0);
                if (yes) _ = RecordAsync(sid, host);
            });
        }
        catch (Exception ex) { Logger.Instance.Warn("Website request check skipped: " + ex.Message); }
    }

    private async Task RecordAsync(string sid, string host)
    {
        try
        {
            var added = await _db.AddSiteRequestAsync(new SiteRequest { UserSid = sid, Account = Environment.UserName, Host = host });
            Logger.Instance.Info($"Asked the parent to allow {host}.");
            ChildNotice.Show(added ? $"Your parent has been asked about {host}. You will be told their answer."
                                   : $"Your parent has already been asked about {host}. Please wait for their answer.");
        }
        catch (Exception ex) { Logger.Instance.Warn("Could not record the website request: " + ex.Message); }
    }

    // The parent answered (from the alert or the Limits page): tell the child, once.
    private async Task TellAnswersAsync()
    {
        try
        {
            var sid = AccountIdentity.CurrentSid;
            if (sid.Length == 0) return;
            foreach (var request in (await _db.GetSiteRequestsAsync(null, sid)).Where(r => !r.Told && r.State != "waiting"))
            {
                if (!await _db.MarkSiteRequestToldAsync(request.Id)) continue;
                ChildNotice.Show(request.State == "allowed"
                    ? $"Your parent allowed {request.Host}. It opens within a minute."
                    : $"Your parent did not allow {request.Host} this time.");
            }
        }
        catch (Exception ex) { Logger.Instance.Warn("Could not check website answers: " + ex.Message); }
    }

    /// <summary>
    /// After the parent saved a website rule: the waiting requests for that site (from that account, or from anyone
    /// for a rule for everyone) are answered "allowed" if the site now really opens for the child. Returns how many
    /// requests are still blocked by another rule (a block, a used-up allowance), so the page can say so.
    /// </summary>
    public static async Task<int> AnswerOpenedAsync(UsageDatabase db, string ruleHost, string? userSid)
    {
        var stillBlocked = 0;
        // While the limits are protected (and not open for editing) the saved rule is put back within seconds.
        if (LimitsSealStore.Phase(LimitsSealStore.Read(), DateTimeOffset.UtcNow) is SealPhase.Enforce or SealPhase.SealNow) return 0;
        var plans = new Dictionary<string, PolicyPlan>();
        foreach (var request in (await db.GetSiteRequestsAsync("waiting", userSid)).Where(r => SiteBlocking.Covers(ruleHost, r.Host)))
        {
            if (!plans.TryGetValue(request.UserSid, out var plan))
                plans[request.UserSid] = plan = await SiteBlockingService.PlanForAsync(db, request.UserSid);
            if (SiteBlocking.IsBlocked(plan, request.Host)) stillBlocked++;
            else await db.AnswerSiteRequestAsync(request.Id, allowed: true);
        }
        return stillBlocked;
    }

    public void Dispose() => _timer.Dispose();
}
