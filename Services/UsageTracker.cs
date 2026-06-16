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
        try
        {
            if (!_running) return;
            await RecordSwitchAsync();
            _lastApp = app;
            _lastProc = proc;
            _lastTick = Environment.TickCount64;
            Logger.Instance.Info($"Tracking: {app} ({proc})");
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Window change tracking failed: {ex.Message}");
        }
    }

    private async Task RecordSwitchAsync()
    {
        if (_lastApp == null) return;
        var now = Environment.TickCount64;
        var elapsed = now - _lastTick;
        if (elapsed is < 1000 or > 60000) return;
        var secs = (int)(elapsed / 1000);
        if (secs > 0)
            await _db.RecordUsageAsync(_lastApp, _lastProc ?? "unknown", secs);
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
            var bonusByApp = (await _db.GetTodayBonusTimeAsync())
                .ToDictionary(b => b.AppName, b => b.BonusMinutes, StringComparer.OrdinalIgnoreCase);
            var breached = new List<(string AppName, long UsedSecs, long MaxSecs)>();

            foreach (var limit in limits.Where(l => l.Enabled))
            {
                var usage = todayUsage.FirstOrDefault(u =>
                    u.AppName.Equals(limit.AppName, StringComparison.OrdinalIgnoreCase));
                var usedSecs = usage?.TotalSeconds ?? 0;
                bonusByApp.TryGetValue(limit.AppName, out var bonusMinutes);
                var maxSecs = (limit.DailyMaxMinutes + bonusMinutes) * 60L;
                if (usedSecs >= maxSecs)
                    breached.Add((limit.AppName, usedSecs, maxSecs));
            }

            var knownAppNames = new HashSet<string>(
                _windowTracker.KnownApps.Values,
                StringComparer.OrdinalIgnoreCase);
            var scheduleViolationApps = await _scheduler.GetViolatingAppNamesAsync(knownAppNames);

            await _limitEnforcer.EnforceAsync(breached, scheduleViolationApps, knownAppNames);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Usage flush failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        _flushTimer?.Dispose();
    }
}
