using MonitorAndControl.Data;
using MonitorAndControl.Models;

namespace MonitorAndControl.Services;

public class UsageTracker : IDisposable
{
    private readonly UsageDatabase _db;
    private readonly WindowTracker _windowTracker;
    private readonly LimitEnforcer _limitEnforcer;
    private readonly SchedulerService _scheduler;

    private string? _lastApp;
    private string? _lastProc;
    private long _lastTick;
    private bool _running;
    private readonly System.Threading.Timer _flushTimer;

    public UsageTracker(UsageDatabase db, WindowTracker wt, LimitEnforcer enforcer, SchedulerService scheduler)
    {
        _db = db;
        _windowTracker = wt;
        _limitEnforcer = enforcer;
        _scheduler = scheduler;
        _flushTimer = new System.Threading.Timer(OnFlush, null, Timeout.Infinite, Timeout.Infinite);
        _windowTracker.OnActiveWindowChanged += OnWindowChanged;
    }

    public void Start(int flushIntervalSec = 30)
    {
        _running = true;
        _lastTick = Environment.TickCount64;
        _flushTimer.Change(flushIntervalSec * 1000, flushIntervalSec * 1000);
    }

    public void Stop()
    {
        _running = false;
        _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _windowTracker.OnActiveWindowChanged -= OnWindowChanged;
        FlushAsync().GetAwaiter().GetResult();
    }

    private async void OnWindowChanged(string app, string proc)
    {
        if (!_running) return;
        await RecordSwitchAsync();
        _lastApp = app;
        _lastProc = proc;
        _lastTick = Environment.TickCount64;
        Logger.Instance.Info($"Tracking: {app} ({proc})");
    }

    private async Task RecordSwitchAsync()
    {
        if (_lastApp == null) return;
        var now = Environment.TickCount64;
        var elapsed = now - _lastTick;
        if (elapsed is < 1000 or > 60000) return;
        var secs = (int)(elapsed / 1000);
        if (secs > 0)
        {
            await _db.RecordUsageAsync(_lastApp, _lastProc ?? "unknown", secs);
            Logger.Instance.Info($"Recorded {secs}s for {_lastApp}");
        }
    }

    private async Task FlushAsync()
    {
        await RecordSwitchAsync();
        _lastTick = Environment.TickCount64;
    }

    private async void OnFlush(object? state)
    {
        if (!_running) return;
        try
        {
            await FlushAsync();

            var limits = await _db.GetLimitRulesAsync();
            var todayUsage = await _db.GetTodayUsageAsync();
            var scheduleViolation = await _scheduler.IsInViolationPeriodAsync();

            var breached = new List<(string AppName, long UsedSecs, long MaxSecs)>();

            foreach (var limit in limits.Where(l => l.Enabled))
            {
                var usage = todayUsage.FirstOrDefault(u =>
                    u.AppName.Equals(limit.AppName, StringComparison.OrdinalIgnoreCase));
                var usedSecs = usage?.TotalSeconds ?? 0;
                var maxSecs = limit.DailyMaxMinutes * 60L;
                if (usedSecs >= maxSecs)
                    breached.Add((limit.AppName, usedSecs, maxSecs));
            }

            var knownAppNames = new HashSet<string>(
                _windowTracker.KnownApps.Values,
                StringComparer.OrdinalIgnoreCase);

            await _limitEnforcer.EnforceAsync(breached, scheduleViolation, knownAppNames);
        }
        catch
        {
            // Silently ignore flush errors
        }
    }

    public void Dispose()
    {
        Stop();
        _flushTimer?.Dispose();
    }
}
