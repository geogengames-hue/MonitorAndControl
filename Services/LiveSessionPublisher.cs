using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

/// <summary>
/// Every signed-in account runs its own DeviceMon, but only one of them serves the dashboard. Each one reports here
/// what its account is doing, so the dashboard can say whose session it is showing and what the other accounts are using.
/// </summary>
public sealed class LiveSessionPublisher : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    /// <summary>A report older than this means the account signed out or its DeviceMon stopped.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly UsageDatabase _db;
    private readonly WindowTracker _tracker;
    private System.Threading.Timer? _timer;

    public LiveSessionPublisher(UsageDatabase db, WindowTracker tracker)
    {
        _db = db;
        _tracker = tracker;
    }

    public void Start() => _timer = new System.Threading.Timer(_ => _ = PublishAsync(), null, TimeSpan.Zero, Interval);

    public async Task PublishAsync()
    {
        try
        {
            var sid = AccountIdentity.CurrentSid;
            if (string.IsNullOrEmpty(sid)) return;
            _tracker.IsUsageTrackingSuspended(out var state);
            await _db.PublishLiveSessionAsync(sid, Environment.UserName, _tracker.CurrentAppName ?? "", _tracker.CurrentProcessName ?? "", state);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn($"Could not publish the live session: {ex.Message}");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
