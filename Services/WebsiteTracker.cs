using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

/// <summary>
/// Adds up how long each website is in the foreground of a browser, without any browser extension.
/// Time counts only while the user is active (the same idle and lock rules as app usage), and only the
/// host name is stored (never paths, queries or page titles). Private windows are one anonymous bucket.
/// </summary>
public sealed class WebsiteTracker : IDisposable
{
    private const int SampleSeconds = 5;
    private const int FlushSeconds = 30;

    private readonly UsageDatabase _db;
    private readonly WindowTracker _tracker;
    private readonly BrowserAddressReader _reader = new();
    private readonly Dictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly System.Threading.Timer _timer;
    private long _lastSampleTick;
    private long _lastFlushTick;
    private volatile bool _enabled = true;
    private int _sampling;

    public WebsiteTracker(UsageDatabase db, WindowTracker tracker)
    {
        _db = db;
        _tracker = tracker;
        _timer = new System.Threading.Timer(Sample, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _lastSampleTick = _lastFlushTick = Environment.TickCount64;
        _timer.Change(TimeSpan.FromSeconds(SampleSeconds), TimeSpan.FromSeconds(SampleSeconds));
    }

    private async void Sample(object? state)
    {
        if (Interlocked.Exchange(ref _sampling, 1) != 0) return;
        try
        {
            var now = Environment.TickCount64;
            var elapsed = (int)Math.Clamp(Math.Round((now - _lastSampleTick) / 1000.0), 0, 2 * SampleSeconds);
            _lastSampleTick = now;

            if (now - _lastFlushTick >= FlushSeconds * 1000L)
            {
                _lastFlushTick = now;
                _enabled = await _db.GetSettingAsync("TrackWebsites", "true") == "true";
                await FlushAsync();
            }
            if (!_enabled || elapsed <= 0) return;

            var site = CurrentSite();
            if (site == null) return;
            lock (_sync) _pending[site] = _pending.GetValueOrDefault(site) + elapsed;
        }
        catch (Exception ex) { Logger.Instance.Warn($"Website tracking skipped a sample: {ex.Message}"); }
        finally { Volatile.Write(ref _sampling, 0); }
    }

    /// <summary>The site in the foreground browser right now, or null when no browser is being used.</summary>
    private string? CurrentSite()
    {
        if (_tracker.IsUsageTrackingSuspended(out _)) return null;
        var process = _tracker.CurrentProcessName;
        var window = _tracker.CurrentWindowHandle;
        if (!BrowserAddressReader.IsBrowser(process) || window == IntPtr.Zero || NativeMethods.GetForegroundWindow() != window)
            return null;

        var title = new System.Text.StringBuilder(512);
        NativeMethods.GetWindowText(window, title, title.Capacity);
        var isPrivate = SiteName.IsPrivateTitle(title.ToString());

        // UI Automation can stall while a browser is busy; give up after two seconds rather than block.
        var read = Task.Run(() => _reader.Read(window, process!));
        // Private windows are read like any other, so a site's daily limit also counts time spent in them;
        // the anonymous bucket is only the fallback when the address bar cannot be read.
        var fallback = isPrivate ? SiteName.PrivateWindow : SiteName.UnknownSite;
        if (!read.Wait(TimeSpan.FromSeconds(2))) return fallback;
        return SiteName.TryGetHost(read.Result, out var host) ? host : fallback;
    }

    public async Task FlushAsync()
    {
        Dictionary<string, long> batch;
        lock (_sync)
        {
            if (_pending.Count == 0) return;
            batch = new Dictionary<string, long>(_pending);
            _pending.Clear();
        }
        try { await _db.AddSiteUsageAsync(MonitorAndControl.ScreenTime.ParentClock.Today, batch, AccountIdentity.CurrentSid); }
        catch (Exception ex)
        {
            lock (_sync)
                foreach (var (host, seconds) in batch) _pending[host] = _pending.GetValueOrDefault(host) + seconds;
            Logger.Instance.Warn($"Could not save website usage yet: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        try { FlushAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
