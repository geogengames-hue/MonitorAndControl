using System.Text.Json;

namespace MonitorAndControl.ScreenTime;

/// <summary>The time zone the rules run in, kept by the SYSTEM service in timezone.json.</summary>
public sealed class TimeZoneState
{
    /// <summary>The zone bedtime, schedules and daily limits use.</summary>
    public string PinnedId { get; set; } = "";
    /// <summary>The zone Windows reported at the last check.</summary>
    public string SystemId { get; set; } = "";
    /// <summary>When Windows started reporting a zone other than the pinned one (null while they match).</summary>
    public DateTimeOffset? ChangedUtc { get; set; }
}

/// <summary>
/// Any Windows account may change the time zone, which would move bedtime and start a new day early.
/// The service pins the zone it first saw; the rules keep using it until the parent accepts a new one.
/// </summary>
public static class ParentClock
{
    private static readonly object Sync = new();
    private static TimeZoneInfo? _zone;
    private static long _readAt = long.MinValue;

    public static string Path => System.IO.Path.Combine(ScreenTimeFiles.DirectoryPath, "timezone.json");

    public static TimeZoneState? Read(string? path = null)
    {
        try
        {
            path ??= Path;
            return File.Exists(path) ? JsonSerializer.Deserialize<TimeZoneState>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    /// <summary>The pinned zone, or the Windows zone when nothing is pinned (no service installed).</summary>
    public static TimeZoneInfo Resolve(TimeZoneState? state)
    {
        if (!string.IsNullOrEmpty(state?.PinnedId))
            try { return TimeZoneInfo.FindSystemTimeZoneById(state.PinnedId); } catch { /* unknown id: fall back */ }
        return TimeZoneInfo.Local;
    }

    public static TimeZoneInfo Zone
    {
        get
        {
            lock (Sync)
            {
                if (_zone == null || Environment.TickCount64 - _readAt > 30000)
                {
                    _zone = Resolve(Read());
                    _readAt = Environment.TickCount64;
                }
                return _zone;
            }
        }
    }

    public static void Invalidate() { lock (Sync) _readAt = long.MinValue; }

    public static DateTime ToRuleTime(DateTimeOffset moment) => TimeZoneInfo.ConvertTime(moment, Zone).DateTime;
    public static DateTime Now => ToRuleTime(DateTimeOffset.UtcNow);
    public static DateTime Today => Now.Date;

    /// <summary>The next state after Windows reports <paramref name="systemId"/>. The first zone seen is pinned.</summary>
    public static TimeZoneState Observe(TimeZoneState? current, string systemId, DateTimeOffset now)
    {
        if (current == null || string.IsNullOrEmpty(current.PinnedId))
            return new TimeZoneState { PinnedId = systemId, SystemId = systemId };
        var changed = systemId == current.PinnedId ? null
            : current.SystemId == systemId && current.ChangedUtc != null ? current.ChangedUtc : now;
        return new TimeZoneState { PinnedId = current.PinnedId, SystemId = systemId, ChangedUtc = changed };
    }

    /// <summary>The parent accepted the zone Windows uses now.</summary>
    public static TimeZoneState Accept(string systemId) => new() { PinnedId = systemId, SystemId = systemId };
}

/// <summary>The optional "check-in" address (for example a healthchecks.io ping URL) the service calls every few minutes.</summary>
public static class Heartbeat
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>An https address on a public host; empty means "off".</summary>
    public static bool IsValidUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return true;
        if (url.Length > 300 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".localhost")) return false;
        if (System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)) return IsPublicAddress(ip);
        return host.Contains('.');
    }

    /// <summary>
    /// False for addresses inside the home network or the PC itself. Checked for literal addresses and again
    /// for every address a name resolves to, so the check-in cannot be pointed at the router or a local service.
    /// </summary>
    public static bool IsPublicAddress(System.Net.IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)
            || ip.Equals(System.Net.IPAddress.IPv6None) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast)
            return false;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return true;
        var b = ip.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }
}
