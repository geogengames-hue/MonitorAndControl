namespace MonitorAndControl.Models;

/// <summary>A child's "please let me open this site" request, made from a blocked website.</summary>
public class SiteRequest
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserSid { get; set; } = "";
    public string Account { get; set; } = "";
    public string Host { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>"waiting", "allowed" or "declined".</summary>
    public string State { get; set; } = "waiting";
    /// <summary>The parent has been sent an alert about it.</summary>
    public bool Alerted { get; set; }
    /// <summary>The child has been told the answer.</summary>
    public bool Told { get; set; } = true;
}
