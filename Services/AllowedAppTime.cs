using MonitorAndControl.ScreenTime;

namespace MonitorAndControl.Services;

/// <summary>
/// Time in an always-allowed app (a school app, a music tutor) does not use up PC time. Every few seconds, while one
/// is in front, DeviceMon tells the SYSTEM service; the service stops counting for a few seconds after each report.
/// If DeviceMon stops (closed, killed), the reports stop and the time is counted again.
/// </summary>
public sealed class AllowedAppTime : IDisposable
{
    private readonly WindowTracker _tracker;
    private readonly LimitEnforcer _enforcer;
    private readonly System.Threading.Timer _timer;
    private int _running;
    // The last program file checked, so its built-in name is not read again every few seconds.
    private (string Path, DateTime Written, bool Genuine) _lastChecked = ("", default, false);

    public AllowedAppTime(WindowTracker tracker, LimitEnforcer enforcer)
    {
        _tracker = tracker;
        _enforcer = enforcer;
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        if (AccountIdentity.IsAdministrator) return;
        _timer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            if (_tracker.CurrentProcessName is not { } processName) return;
            var appName = _tracker.KnownApps.TryGetValue(processName, out var friendly) ? friendly : Path.GetFileNameWithoutExtension(processName);
            if (!_enforcer.IsAlwaysAllowed(appName) || !Genuine(processName)) return;
            var reply = await ScreenTimeClient.SendAsync(new ScreenTimeRequest { Command = "pc-time-exempt" }, CancellationToken.None);
            if (!reply.Ok) Logger.Instance.Warn("Could not report time in an always-allowed app: " + reply.Error);
        }
        catch (Exception ex) { Logger.Instance.Warn("Always-allowed app time skipped: " + ex.Message); }
        finally { Volatile.Write(ref _running, 0); }
    }

    // Only the real program counts: a game renamed to an always-allowed name is known by the name built into it.
    private bool Genuine(string processName)
    {
        var path = ProgramGate.ProgramPath(processName);
        if (path == null) return false;
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(path); } catch { return false; }
        if (string.Equals(_lastChecked.Path, path, StringComparison.OrdinalIgnoreCase) && _lastChecked.Written == written) return _lastChecked.Genuine;
        // Apps built on Electron (VS Code, Teams, ...) all carry the name electron.exe.
        var genuine = ProgramGate.IdentityOf(processName, path) is { } key && (key == ProgramApprovals.Key(processName) || key == "electron");
        _lastChecked = (path, written, genuine);
        return genuine;
    }

    public void Dispose() => _timer.Dispose();
}
