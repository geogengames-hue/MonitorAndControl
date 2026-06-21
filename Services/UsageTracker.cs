using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

public class UsageTracker : IDisposable
{
    private readonly UsageDatabase _db;
    private readonly WindowTracker _windowTracker;
    private readonly LimitEnforcer _limitEnforcer;
    private readonly SchedulerService _scheduler;
    private readonly System.Threading.Timer _sampleTimer;
    private readonly System.Threading.Timer _flushTimer;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly object _usageSync = new();
    private readonly Dictionary<string, AccumulatedUsage> _pendingUsage =
        new(StringComparer.OrdinalIgnoreCase);

    private long _lastSampleTick;
    private int _sampling;
    private bool _running;

    public UsageTracker(UsageDatabase db, WindowTracker wt, LimitEnforcer enforcer, SchedulerService scheduler)
    {
        _db = db;
        _windowTracker = wt;
        _limitEnforcer = enforcer;
        _scheduler = scheduler;
        _sampleTimer = new System.Threading.Timer(OnSample, null, Timeout.Infinite, Timeout.Infinite);
        _flushTimer = new System.Threading.Timer(OnFlush, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start(int flushIntervalSec = 30)
    {
        _running = true;
        _lastSampleTick = Environment.TickCount64;
        _sampleTimer.Change(1000, 1000);
        _flushTimer.Change(flushIntervalSec * 1000, flushIntervalSec * 1000);
    }

    public void Stop()
    {
        if (!_running) return;
        _sampleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SampleUsage();
        _running = false;
        FlushAsync().GetAwaiter().GetResult();
    }

    private void OnSample(object? state)
    {
        if (!_running || Interlocked.Exchange(ref _sampling, 1) != 0) return;
        try
        {
            SampleUsage();
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Usage sampling failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    private void SampleUsage()
    {
        var now = Environment.TickCount64;
        var elapsedMs = now - Interlocked.Exchange(ref _lastSampleTick, now);
        if (elapsedMs is < 250 or > 5000)
            return;

        var activeApps = new Dictionary<string, ActiveUsage>(StringComparer.OrdinalIgnoreCase);
        foreach (var backgroundApp in _windowTracker.GetRunningBackgroundApps())
            activeApps[backgroundApp.AppName] = new ActiveUsage(backgroundApp.ProcessName, IsForeground: false);

        // Foreground wins when an app is also enabled for background tracking.
        // This keeps total time additive instead of counting the same interval twice.
        if (!string.IsNullOrWhiteSpace(_windowTracker.CurrentAppName))
            activeApps[_windowTracker.CurrentAppName] = new ActiveUsage(
                _windowTracker.CurrentProcessName ?? "unknown",
                IsForeground: true);

        lock (_usageSync)
        {
            foreach (var active in activeApps)
            {
                if (!_pendingUsage.TryGetValue(active.Key, out var usage))
                    usage = new AccumulatedUsage(active.Value.ProcessName, 0, 0);
                _pendingUsage[active.Key] = usage with
                {
                    ProcessName = active.Value.ProcessName,
                    ForegroundMilliseconds = usage.ForegroundMilliseconds +
                        (active.Value.IsForeground ? elapsedMs : 0),
                    BackgroundMilliseconds = usage.BackgroundMilliseconds +
                        (active.Value.IsForeground ? 0 : elapsedMs)
                };
            }
        }
    }

    private async Task FlushAsync()
    {
        await _flushLock.WaitAsync();
        try
        {
            List<UsageFlushRecord> records;
            lock (_usageSync)
            {
                records = _pendingUsage
                    .Where(kvp => kvp.Value.ForegroundMilliseconds >= 1000 ||
                                  kvp.Value.BackgroundMilliseconds >= 1000)
                    .Select(kvp => new UsageFlushRecord(
                        kvp.Key,
                        kvp.Value.ProcessName,
                        (int)(kvp.Value.ForegroundMilliseconds / 1000),
                        (int)(kvp.Value.BackgroundMilliseconds / 1000)))
                    .ToList();
                foreach (var record in records)
                {
                    var usage = _pendingUsage[record.AppName];
                    _pendingUsage[record.AppName] = usage with
                    {
                        ForegroundMilliseconds = usage.ForegroundMilliseconds - record.ForegroundSeconds * 1000L,
                        BackgroundMilliseconds = usage.BackgroundMilliseconds - record.BackgroundSeconds * 1000L
                    };
                }
            }

            try
            {
                foreach (var record in records)
                    await _db.RecordUsageAsync(
                        record.AppName,
                        record.ProcessName,
                        record.ForegroundSeconds,
                        record.BackgroundSeconds);
            }
            catch
            {
                lock (_usageSync)
                {
                    foreach (var record in records)
                    {
                        var usage = _pendingUsage.TryGetValue(record.AppName, out var current)
                            ? current
                            : new AccumulatedUsage(record.ProcessName, 0, 0);
                        _pendingUsage[record.AppName] = usage with
                        {
                            ForegroundMilliseconds = usage.ForegroundMilliseconds + record.ForegroundSeconds * 1000L,
                            BackgroundMilliseconds = usage.BackgroundMilliseconds + record.BackgroundSeconds * 1000L
                        };
                    }
                }
                throw;
            }

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
        finally
        {
            _flushLock.Release();
        }
    }

    private async void OnFlush(object? state)
    {
        if (!_running) return;
        try
        {
            SampleUsage();
            await FlushAsync();
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Usage flush failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        _sampleTimer.Dispose();
        _flushTimer.Dispose();
        _flushLock.Dispose();
    }

    private readonly record struct ActiveUsage(string ProcessName, bool IsForeground);
    private readonly record struct AccumulatedUsage(
        string ProcessName,
        long ForegroundMilliseconds,
        long BackgroundMilliseconds);
    private readonly record struct UsageFlushRecord(
        string AppName,
        string ProcessName,
        int ForegroundSeconds,
        int BackgroundSeconds);
}
