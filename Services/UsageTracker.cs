using MonitorAndControl.Data;
using MonitorAndControl.Models;

namespace MonitorAndControl.Services;

public class UsageTracker : IDisposable
{
    private readonly UsageDatabase _db;
    private readonly WindowTracker _windowTracker;
    private readonly LimitEnforcer _limitEnforcer;
    private readonly SchedulerService _scheduler;
    private readonly AppCategories _categories;
    private readonly System.Threading.Timer _sampleTimer;
    private readonly System.Threading.Timer _flushTimer;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly object _usageSync = new();
    private readonly Dictionary<string, AccumulatedUsage> _pendingUsage =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, long> _pendingGroupMilliseconds = new();
    private List<AppLimitGroup> _limitGroups = new();

    private long _lastSampleTick;
    private int _sampling;
    private bool _running;

    public UsageTracker(UsageDatabase db, WindowTracker wt, LimitEnforcer enforcer, SchedulerService scheduler, AppCategories categories)
    {
        _db = db;
        _windowTracker = wt;
        _limitEnforcer = enforcer;
        _scheduler = scheduler;
        _categories = categories;
        _sampleTimer = new System.Threading.Timer(OnSample, null, Timeout.Infinite, Timeout.Infinite);
        _flushTimer = new System.Threading.Timer(OnFlush, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start(int flushIntervalSec = 30)
    {
        _limitGroups = ForThisAccount(_db.GetLimitGroupsAsync().GetAwaiter().GetResult());
        SetGroupState(_limitGroups);
        _running = true;
        _lastSampleTick = Environment.TickCount64;
        _sampleTimer.Change(1000, 1000);
        _flushTimer.Change(flushIntervalSec * 1000, flushIntervalSec * 1000);
    }

    // A group set for one Windows account is counted and enforced only by that account's DeviceMon.
    private static List<AppLimitGroup> ForThisAccount(List<AppLimitGroup> groups) =>
        groups.Where(g => AccountIdentity.AppliesHere(g.UserSid)).ToList();

    public async Task ReloadLimitGroupsAsync()
    {
        var groups = ForThisAccount(await _db.GetLimitGroupsAsync());
        lock (_usageSync)
            _limitGroups = groups;
        SetGroupState(groups);
    }

    // Which of this account's groups have used up today's time (for closing an app of a used-up category at once).
    private void SetGroupState(List<AppLimitGroup> groups) =>
        _limitEnforcer.SetGroupState(groups, groups.Where(g => g.Enabled && g.TodaySeconds >= g.DailyMaxMinutes * 60L).Select(g => g.Id));

    /// <summary>
    /// Today's usage per app: everyone's added up, or (with <paramref name="userSid"/>) one Windows account's own time.
    /// For the combined view each record also says how much the busiest single account used, which is what a limit counts.
    /// </summary>
    public async Task<List<AppUsageRecord>> GetTodayUsageIncludingPendingAsync(string? userSid = null)
    {
        var today = MonitorAndControl.ScreenTime.ParentClock.Today;
        var perAccount = await _db.GetAllUserAppUsageTodayAsync();
        List<AppUsageRecord> usage;
        if (string.IsNullOrEmpty(userSid))
            usage = await _db.GetTodayUsageAsync();
        else
        {
            var processNames = (await _db.GetTodayUsageAsync()).ToDictionary(r => r.AppName, r => r.ProcessName, StringComparer.OrdinalIgnoreCase);
            usage = perAccount.Where(u => u.UserSid == userSid).Select(u => new AppUsageRecord
            {
                AppName = u.AppName,
                ProcessName = processNames.GetValueOrDefault(u.AppName) ?? "",
                Date = today,
                TotalSeconds = u.Total,
                ForegroundSeconds = u.Foreground,
                BackgroundSeconds = u.Background
            }).ToList();
        }

        List<(string AppName, string ProcessName, int ForegroundSeconds, int BackgroundSeconds)> pending;
        lock (_usageSync)
        {
            pending = _pendingUsage
                .Select(kvp => (
                    AppName: kvp.Key,
                    ProcessName: kvp.Value.ProcessName,
                    ForegroundSeconds: (int)(kvp.Value.ForegroundMilliseconds / 1000),
                    BackgroundSeconds: (int)(kvp.Value.BackgroundMilliseconds / 1000)))
                .Where(item => item.ForegroundSeconds > 0 || item.BackgroundSeconds > 0)
                .ToList();
        }
        // Time not saved yet belongs to the account this DeviceMon runs for.
        if (!string.IsNullOrEmpty(userSid) && userSid != AccountIdentity.CurrentSid)
            pending.Clear();

        // The busiest single account per app: a limit counts each account's own time, not the combined total.
        var perApp = new Dictionary<(string App, string Sid), long>();
        foreach (var u in perAccount)
        {
            var key = (u.AppName.ToLowerInvariant(), u.UserSid);
            perApp[key] = perApp.GetValueOrDefault(key) + u.Total;
        }
        foreach (var p in pending)
        {
            var key = (p.AppName.ToLowerInvariant(), AccountIdentity.CurrentSid);
            perApp[key] = perApp.GetValueOrDefault(key) + p.ForegroundSeconds + p.BackgroundSeconds;
        }
        var highest = perApp.GroupBy(kv => kv.Key.App).ToDictionary(g => g.Key, g => g.Max(kv => kv.Value));

        foreach (var item in pending)
        {
            var existing = usage.FirstOrDefault(record =>
                record.AppName.Equals(item.AppName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                usage.Add(new AppUsageRecord
                {
                    AppName = item.AppName,
                    ProcessName = item.ProcessName,
                    Date = MonitorAndControl.ScreenTime.ParentClock.Today
                });
                existing = usage[^1];
            }

            existing.ProcessName = item.ProcessName;
            existing.ForegroundSeconds += item.ForegroundSeconds;
            existing.BackgroundSeconds += item.BackgroundSeconds;
            existing.TotalSeconds += item.ForegroundSeconds + item.BackgroundSeconds;
        }

        if (string.IsNullOrEmpty(userSid))
            foreach (var record in usage)
                if (highest.TryGetValue(record.AppName.ToLowerInvariant(), out var seconds))
                    record.HighestAccountSeconds = seconds;

        return usage.OrderByDescending(record => record.TotalSeconds).ToList();
    }

    public async Task<List<AppLimitGroup>> GetLimitGroupsIncludingPendingAsync()
    {
        var groups = await _db.GetLimitGroupsAsync();
        Dictionary<int, int> pending;
        lock (_usageSync)
        {
            pending = _pendingGroupMilliseconds
                .Where(kvp => kvp.Value >= 1000)
                .ToDictionary(kvp => kvp.Key, kvp => (int)(kvp.Value / 1000));
        }

        foreach (var group in groups)
        {
            if (pending.TryGetValue(group.Id, out var seconds))
                group.TodaySeconds += seconds;
        }

        return groups;
    }

    public async Task<List<LimitGroupUsageRecord>> GetTodayGroupUsageIncludingPendingAsync()
    {
        var records = await _db.GetLimitGroupUsageRangeAsync(MonitorAndControl.ScreenTime.ParentClock.Today, MonitorAndControl.ScreenTime.ParentClock.Today);
        var groups = await GetLimitGroupsIncludingPendingAsync();
        foreach (var group in groups)
        {
            var record = records.FirstOrDefault(item => item.GroupId == group.Id);
            if (record == null && group.TodaySeconds > 0)
            {
                records.Add(new LimitGroupUsageRecord
                {
                    GroupId = group.Id,
                    GroupName = group.Name,
                    Date = MonitorAndControl.ScreenTime.ParentClock.Today,
                    TotalSeconds = group.TodaySeconds
                });
            }
            else if (record != null)
            {
                record.TotalSeconds = group.TodaySeconds;
                record.GroupName = group.Name;
            }
        }

        return records.OrderByDescending(record => record.TotalSeconds).ToList();
    }

    public async Task ResetTodayAsync()
    {
        await _flushLock.WaitAsync();
        try
        {
            lock (_usageSync)
            {
                _pendingUsage.Clear();
                _pendingGroupMilliseconds.Clear();
                _lastSampleTick = Environment.TickCount64;
            }
            await _db.ClearTodayUsageAsync();
        }
        finally { _flushLock.Release(); }
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

        if (_windowTracker.IsUsageTrackingSuspended(out _))
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

        // Looked up outside the lock: the first time an app is seen, its program file is read.
        var categories = activeApps.ToDictionary(a => a.Key,
            a => _categories.CategoryOf(a.Key, a.Value.ProcessName == "unknown" ? null : a.Value.ProcessName), StringComparer.OrdinalIgnoreCase);

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

            // Each group counts once, however many of its apps run together.
            var counted = activeApps.Keys
                .SelectMany(app => AppCategories.GroupsFor(app, categories[app], _limitGroups))
                .Select(g => g.Id).ToHashSet();
            foreach (var groupId in counted)
            {
                _pendingGroupMilliseconds.TryGetValue(groupId, out var pending);
                _pendingGroupMilliseconds[groupId] = pending + elapsedMs;
            }
        }
    }

    private async Task FlushAsync()
    {
        await _flushLock.WaitAsync();
        try
        {
            var currentGroups = ForThisAccount(await _db.GetLimitGroupsAsync());
            List<UsageFlushRecord> records;
            List<(int GroupId, int Seconds)> groupRecords;
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
                var currentGroupIds = currentGroups.Select(g => g.Id).ToHashSet();
                groupRecords = _pendingGroupMilliseconds
                    .Where(kvp => currentGroupIds.Contains(kvp.Key) && kvp.Value >= 1000)
                    .Select(kvp => (kvp.Key, (int)(kvp.Value / 1000)))
                    .ToList();
                foreach (var record in groupRecords)
                    _pendingGroupMilliseconds[record.GroupId] -= record.Seconds * 1000L;
                foreach (var staleId in _pendingGroupMilliseconds.Keys.Where(id => !currentGroupIds.Contains(id)).ToList())
                    _pendingGroupMilliseconds.Remove(staleId);
                _limitGroups = currentGroups;
            }

            try
            {
                await _db.RecordUsageBatchAsync(
                    records.Select(record => (record.AppName, record.ProcessName, record.ForegroundSeconds, record.BackgroundSeconds)),
                    groupRecords,
                    AccountIdentity.CurrentSid);
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
                    foreach (var record in groupRecords)
                    {
                        _pendingGroupMilliseconds.TryGetValue(record.GroupId, out var pending);
                        _pendingGroupMilliseconds[record.GroupId] = pending + record.Seconds * 1000L;
                    }
                }
                throw;
            }

            // Every limit, shared or for this account alone, is counted from this Windows account's own usage.
            var breached = (await AccountAllowances.ForAccountAsync(_db, AccountIdentity.CurrentSid))
                .Where(a => a.Used >= a.Max)
                .Select(a => (AppName: a.App, UsedSecs: a.Used, MaxSecs: a.Max))
                .ToList();
            var breachedGroups = new List<GroupLimitBreach>();

            await _categories.ReloadAsync(_db);
            var refreshedGroups = ForThisAccount(await _db.GetLimitGroupsAsync());
            var seenApps = _categories.SeenApps();
            foreach (var group in refreshedGroups.Where(g => g.Enabled))
            {
                var maxSecs = group.DailyMaxMinutes * 60L;
                if (group.TodaySeconds < maxSecs) continue;
                // A category group also covers the other apps of its category that ran in this session.
                var categoryApps = group.Category.Length == 0 ? new List<string>() : seenApps
                    .Where(seen => !group.AppNames.Contains(seen.Key, StringComparer.OrdinalIgnoreCase)
                        && AppCategories.GroupsFor(seen.Key, _categories.CategoryOf(seen.Key, seen.Value), refreshedGroups).Any(g => g.Id == group.Id)
                        && _windowTracker.IsProcessRunning(seen.Value))
                    .Select(seen => seen.Key).ToList();
                breachedGroups.Add(new GroupLimitBreach(
                    group.Id, group.Name, group.AppNames, group.TodaySeconds, maxSecs, categoryApps));
            }
            _limitEnforcer.SetGroupState(refreshedGroups, breachedGroups.Select(g => g.GroupId));

            var knownAppNames = new HashSet<string>(
                _windowTracker.KnownApps.Values,
                StringComparer.OrdinalIgnoreCase);
            var scheduleViolationApps = await _scheduler.GetViolatingAppNamesAsync(knownAppNames);

            // Homework mode: the apps the parent listed are closed like an app outside its allowed hours.
            HashSet<string>? homeworkApps = null;
            if (MonitorAndControl.ScreenTime.HomeworkState.IsActiveNow())
                homeworkApps = new HashSet<string>(await _db.GetHomeworkAppsAsync(), StringComparer.OrdinalIgnoreCase);

            _limitEnforcer.SetAlwaysAllowed(await _db.GetAlwaysAllowedAppsAsync());
            await _limitEnforcer.EnforceAsync(breached, breachedGroups, scheduleViolationApps, homeworkApps);
            _limitEnforcer.PruneExceeded(breached.Select(b => b.AppName)
                .Concat(breachedGroups.SelectMany(g => g.AppNames.Concat(g.CategoryApps ?? Array.Empty<string>()))));
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
