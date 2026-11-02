using System.Text.RegularExpressions;

namespace MonitorAndControl.ScreenTime;

/// <summary>A website the parent limits (Minutes &gt; 0 per day) or blocks outright (Minutes = 0).</summary>
public sealed class SealedSite
{
    public string Host { get; set; } = "";
    public int Minutes { get; set; }
    public bool Enabled { get; set; }
    /// <summary>Empty = everyone; otherwise only this Windows account (SID).</summary>
    public string User { get; set; } = "";
}

/// <summary>What the browsers should be told: sites to block, sites to allow (when only listed sites may open) and SafeSearch.</summary>
public sealed record PolicyPlan(List<string> BlockHosts, List<string> AllowHosts, bool AllowOnly, bool SafeBrowsing);

/// <summary>Shared rules for turning site rules into the block lists that browsers read from Windows policy.</summary>
public static class SiteBlocking
{
    public const int MaxHosts = 200;
    private static readonly Regex Valid = new(@"^[a-z0-9]([a-z0-9\-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9\-]*[a-z0-9])?)+$", RegexOptions.Compiled);

    /// <summary>Accepts a site name like youtube.com (a leading www. is dropped); rejects paths, spaces and bare words.</summary>
    public static bool TryNormalizeHost(string? raw, out string host)
    {
        host = (raw ?? "").Trim().ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        if (host.Length is 0 or > 253 || !Valid.IsMatch(host)) { host = ""; return false; }
        return true;
    }

    public static List<string> NormalizeAll(IEnumerable<string>? hosts) =>
        (hosts ?? Enumerable.Empty<string>())
            .Select(h => TryNormalizeHost(h, out var n) ? n : null)
            .Where(h => h != null).Select(h => h!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxHosts).ToList();

    /// <summary>Does a site seen in the browser (m.youtube.com) fall under a rule for youtube.com?</summary>
    public static bool Covers(string ruleHost, string seenHost) =>
        seenHost.Equals(ruleHost, StringComparison.OrdinalIgnoreCase)
        || seenHost.EndsWith("." + ruleHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Combines what DeviceMon reports right now (daily allowances used up, switches) with the sealed copy the
    /// parent approved. The sealed part cannot be cleared by anyone but the parent, whatever DeviceMon reports.
    /// A site that is blocked is never also allowed (an allow entry would win over the block).
    /// Rules for one account count only while that account is at the screen (one of <paramref name="activeSids"/>;
    /// with several active sessions, for example a remote desktop, all of their rules apply).
    /// </summary>
    public static PolicyPlan ComputePlan(IEnumerable<string> reportedBlocked, IEnumerable<string> reportedAllowed,
        bool reportedAllowOnly, bool reportedSafe, SealedLimits? sealedContent, bool homework = false, IReadOnlyCollection<string>? activeSids = null)
    {
        var sealedSites = (sealedContent?.Sites ?? new List<SealedSite>())
            .Where(s => string.IsNullOrEmpty(s.User) || (activeSids?.Contains(s.User) ?? false));
        var sealedBlocked = sealedSites.Where(s => s.Enabled && s.Minutes == 0).Select(s => s.Host);
        var sealedAllowed = sealedSites.Where(s => s.Enabled && s.Minutes != 0).Select(s => s.Host);

        var blocked = NormalizeAll(reportedBlocked.Concat(sealedBlocked));
        // Homework mode behaves like allow-list mode for as long as it lasts.
        var allowOnly = reportedAllowOnly || sealedContent?.AllowOnly == true || homework;
        var safe = reportedSafe || sealedContent?.SafeBrowsing == true;
        var allowed = allowOnly
            ? NormalizeAll(reportedAllowed.Concat(sealedAllowed)).Where(h => !blocked.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList()
            : new List<string>();
        return new PolicyPlan(blocked, allowed, allowOnly, safe);
    }

    /// <summary>Would the browsers refuse this site under the plan?</summary>
    public static bool IsBlocked(PolicyPlan plan, string host) =>
        plan.BlockHosts.Any(rule => Covers(rule, host)) || (plan.AllowOnly && !plan.AllowHosts.Any(rule => Covers(rule, host)));

    /// <summary>Chrome-family browsers: "youtube.com" blocks that site and all its subdomains.</summary>
    public static List<string> ChromiumEntries(IEnumerable<string> hosts) => hosts.ToList();

    /// <summary>Firefox WebsiteFilter match patterns for the same hosts.</summary>
    public static List<string> FirefoxEntries(IEnumerable<string> hosts) =>
        hosts.SelectMany(h => new[] { $"*://{h}/*", $"*://*.{h}/*" }).ToList();

    /// <summary>
    /// Entries that were already in a browser's block list and did not come from DeviceMon are kept;
    /// ours are replaced by the current desired set.
    /// </summary>
    public static List<string> Merge(IEnumerable<string> current, IEnumerable<string> previouslyOwned, IEnumerable<string> desired)
    {
        var owned = new HashSet<string>(previouslyOwned, StringComparer.OrdinalIgnoreCase);
        var result = current.Where(e => !owned.Contains(e)).ToList();
        foreach (var entry in desired)
            if (!result.Contains(entry, StringComparer.OrdinalIgnoreCase)) result.Add(entry);
        return result;
    }
}
