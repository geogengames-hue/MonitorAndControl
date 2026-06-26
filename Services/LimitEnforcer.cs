using System.Diagnostics;
using System.Collections.Concurrent;
using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

public sealed record GroupLimitBreach(
    int GroupId,
    string GroupName,
    IReadOnlyList<string> AppNames,
    long UsedSecs,
    long MaxSecs);

public class LimitEnforcer
{
    private readonly UsageDatabase _db;
    private readonly WindowTracker _tracker;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCountdowns = new();
    private readonly ConcurrentDictionary<string, string> _exceededToday = new(StringComparer.OrdinalIgnoreCase);
    private string _todayDate = DateTime.Now.ToString("yyyy-MM-dd");
    private DateTimeOffset? _pausedUntil;

    public event Action<string, int, string>? OnBreachAlert;
    public event Action<string, int>? OnCountdownTick;
    public event Action<string>? OnAppKilled;
    public event Action<string>? OnAppTerminatedBySchedule;

    public DateTimeOffset? PausedUntil => _pausedUntil;
    public bool IsPaused => _pausedUntil.HasValue && _pausedUntil.Value > DateTimeOffset.Now;

    public LimitEnforcer(UsageDatabase db, WindowTracker tracker)
    {
        _db = db;
        _tracker = tracker;
        _tracker.OnActiveWindowChanged += OnAppChanged;
    }

    public async Task RehydrateExceededTodayAsync()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_todayDate != today)
        {
            _exceededToday.Clear();
            _todayDate = today;
        }

        var limits = await _db.GetLimitRulesAsync();
        var todayUsage = await _db.GetTodayUsageAsync();
        var bonusByApp = (await _db.GetTodayBonusTimeAsync())
            .ToDictionary(b => b.AppName, b => b.BonusMinutes, StringComparer.OrdinalIgnoreCase);

        foreach (var limit in limits.Where(l => l.Enabled))
        {
            var usage = todayUsage.FirstOrDefault(u =>
                u.AppName.Equals(limit.AppName, StringComparison.OrdinalIgnoreCase));
            if (usage == null) continue;

            bonusByApp.TryGetValue(limit.AppName, out var bonusMinutes);
            var maxSecs = (limit.DailyMaxMinutes + bonusMinutes) * 60L;
            if (maxSecs <= 0 || usage.TotalSeconds < maxSecs) continue;

            _exceededToday[limit.AppName] = today;
        }

        var groups = await _db.GetLimitGroupsAsync();
        foreach (var group in groups.Where(g => g.Enabled && g.TodaySeconds >= g.DailyMaxMinutes * 60L))
            foreach (var appName in group.AppNames)
                _exceededToday[appName] = today;
    }

    private void OnAppChanged(string app, string proc)
    {
        // Auto-clear exceeded set at midnight
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_todayDate != today)
        {
            _exceededToday.Clear();
            _todayDate = today;
        }

        // If this app was already killed today for exceeding its limit, block immediately
        if (_exceededToday.ContainsKey(app) && _tracker.IsProcessRunning(proc))
        {
            KillAppProcesses(app);
            OnAppKilled?.Invoke(app);
        }

        // Child closed and reopened app during countdown - kill immediately.
        if (!_exceededToday.ContainsKey(app) && _activeCountdowns.ContainsKey(app.ToLowerInvariant()) && _tracker.IsProcessRunning(proc))
        {
            CancelCountdown(app);
            _exceededToday[app] = today;
            KillAppProcesses(app);
            OnAppKilled?.Invoke(app);
        }
    }

    public Task EnforceAsync(
        List<(string AppName, long UsedSecs, long MaxSecs)> breached,
        List<GroupLimitBreach> breachedGroups,
        HashSet<string> scheduleViolationApps)
    {
        if (IsPaused)
            return Task.CompletedTask;

        if (_pausedUntil.HasValue && _pausedUntil.Value <= DateTimeOffset.Now)
            _pausedUntil = null;

        if (scheduleViolationApps.Count > 0)
        {
            Logger.Instance.Warn("Schedule violation - killing matching tracked processes");
            var runningProcessNames = _tracker.GetRunningProcessNames();
            foreach (var procName in runningProcessNames)
            {
                var appName = _tracker.KnownApps.TryGetValue(procName, out var friendly)
                    ? friendly : procName;
                if (!scheduleViolationApps.Contains(appName))
                    continue;

                Logger.Instance.Warn($"Scheduled kill: {appName} ({procName})");
                KillProcessByName(procName);
                OnAppTerminatedBySchedule?.Invoke(appName);
            }
        }

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_todayDate != today)
        {
            _exceededToday.Clear();
            _todayDate = today;
        }

        var groupMemberApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in breachedGroups)
        {
            var members = group.AppNames
                .Where(appName => _tracker.KnownApps.Values.Any(value =>
                    value.Equals(appName, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var member in members) groupMemberApps.Add(member);
            if (members.Length == 0) continue;

            var key = GroupCountdownKey(group.GroupId);
            if (members.All(IsExceededToday))
            {
                foreach (var member in members.Where(IsAnyAppProcessRunning))
                    KillAppProcesses(member);
                continue;
            }
            if (!members.Any(IsAnyAppProcessRunning))
            {
                foreach (var member in members) _exceededToday[member] = today;
                continue;
            }
            if (!_activeCountdowns.ContainsKey(key))
                _ = StartGroupCountdownAsync(group, members);
        }

        foreach (var (appName, used, max) in breached)
        {
            if (groupMemberApps.Contains(appName)) continue;
            var key = appName.ToLowerInvariant();
            if (!_tracker.KnownApps.Values.Any(v =>
                    v.Equals(appName, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (_exceededToday.ContainsKey(appName))
            {
                if (!_activeCountdowns.ContainsKey(key))
                {
                    if (IsAnyAppProcessRunning(appName))
                    {
                        Logger.Instance.Info($"Re-kill exceeded app: {appName}");
                        KillAppProcesses(appName);
                    }
                }
                continue;
            }

            // First breach - start countdown.
            if (!_activeCountdowns.ContainsKey(key))
                _ = StartCountdownAsync(appName);
        }
        return Task.CompletedTask;
    }

    private async Task StartGroupCountdownAsync(GroupLimitBreach group, string[] members)
    {
        var delay = await _db.GetKillDelayAsync();
        var cts = new CancellationTokenSource();
        var key = GroupCountdownKey(group.GroupId);
        var runningMember = members.FirstOrDefault(IsAnyAppProcessRunning) ?? members[0];
        var procName = _tracker.GetRunningProcessNamesForApp(runningMember).FirstOrDefault()
            ?? _tracker.GetProcessNameForApp(runningMember)
            ?? runningMember;
        var displayName = $"{group.GroupName} (group)";

        if (!_activeCountdowns.TryAdd(key, cts)) return;
        OnBreachAlert?.Invoke(displayName, delay, procName);
        try
        {
            for (var i = delay; i > 0; i--)
            {
                await Task.Delay(1000, cts.Token);
                OnCountdownTick?.Invoke(displayName, i);
            }
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            foreach (var member in members)
            {
                _exceededToday[member] = today;
                KillAppProcesses(member);
            }
            OnAppKilled?.Invoke(displayName);
        }
        catch (TaskCanceledException) { }
        finally { _activeCountdowns.TryRemove(key, out _); }
    }

    private static string GroupCountdownKey(int groupId) => $"group:{groupId}";

    private async Task StartCountdownAsync(string appName)
    {
        var delay = await _db.GetKillDelayAsync();
        var cts = new CancellationTokenSource();
        var key = appName.ToLowerInvariant();
        var procName = _tracker.GetRunningProcessNamesForApp(appName).FirstOrDefault()
            ?? _tracker.GetProcessNameForApp(appName)
            ?? appName;

        if (!_activeCountdowns.TryAdd(key, cts))
            return;

        OnBreachAlert?.Invoke(appName, delay, procName);

        try
        {
            for (int i = delay; i > 0; i--)
            {
                await Task.Delay(1000, cts.Token);
                OnCountdownTick?.Invoke(appName, i);
            }

            _exceededToday[appName] = DateTime.Now.ToString("yyyy-MM-dd");
            KillAppProcesses(appName);
            OnAppKilled?.Invoke(appName);
        }
        catch (TaskCanceledException) { }
        finally
        {
            _activeCountdowns.TryRemove(key, out _);
        }
    }

    public bool IsExceededToday(string appName) => _exceededToday.ContainsKey(appName);

    public void ClearExceeded(string? appName = null)
    {
        if (appName != null)
        {
            _exceededToday.TryRemove(appName, out _);
            CancelCountdown(appName);
        }
        else
        {
            foreach (var key in _activeCountdowns.Keys)
                if (_activeCountdowns.TryRemove(key, out var cts)) cts.Cancel();
            _exceededToday.Clear();
            _todayDate = DateTime.Now.ToString("yyyy-MM-dd");
        }
    }

    public void ClearGroup(int groupId)
    {
        var key = GroupCountdownKey(groupId);
        if (_activeCountdowns.TryRemove(key, out var cts)) cts.Cancel();
    }

    public void CancelCountdown(string appName)
    {
        var key = appName.ToLowerInvariant();
        if (_activeCountdowns.TryRemove(key, out var cts))
            cts.Cancel();
    }

    public bool HasActiveCountdown(string appName)
    {
        return _activeCountdowns.ContainsKey(appName.ToLowerInvariant());
    }

    public DateTimeOffset PauseFor(TimeSpan duration)
    {
        foreach (var key in _activeCountdowns.Keys)
        {
            if (_activeCountdowns.TryRemove(key, out var cts))
                cts.Cancel();
        }

        _pausedUntil = DateTimeOffset.Now.Add(duration);
        Logger.Instance.Warn($"Enforcement paused until {_pausedUntil.Value:yyyy-MM-dd HH:mm:ss zzz}");
        return _pausedUntil.Value;
    }

    public void Resume()
    {
        _pausedUntil = null;
        Logger.Instance.Warn("Enforcement resumed");
    }

    public int KillRunningTrackedApps()
    {
        var killed = 0;
        var runningProcessNames = _tracker.GetRunningProcessNames();
        foreach (var procName in runningProcessNames)
        {
            var appName = _tracker.KnownApps.TryGetValue(procName, out var friendly)
                ? friendly
                : procName;
            KillProcessByName(procName);
            OnAppTerminatedBySchedule?.Invoke(appName);
            killed++;
        }

        Logger.Instance.Warn($"Block-now action closed {killed} tracked app(s)");
        return killed;
    }

    private void KillAppProcesses(string appName)
    {
        try
        {
            var processNames = _tracker.GetProcessNamesForApp(appName);
            if (processNames.Length == 0)
                processNames = new[] { appName };
            foreach (var processName in processNames)
                KillProcessByName(processName);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to kill app processes for {appName}: {ex.Message}");
        }
    }

    private bool IsAnyAppProcessRunning(string appName)
    {
        var processNames = _tracker.GetProcessNamesForApp(appName);
        return processNames.Length > 0
            ? _tracker.GetRunningProcessNamesForApp(appName).Length > 0
            : _tracker.IsProcessRunning(appName);
    }

    private void KillProcessByName(string processName)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(processName);
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    proc.CloseMainWindow();
                    if (!proc.WaitForExit(5000))
                        proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error($"Failed to close process {proc.ProcessName} ({proc.Id}): {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to enumerate processes for {processName}: {ex.Message}");
        }
    }

}
