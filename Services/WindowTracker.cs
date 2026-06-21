using System.Diagnostics;
using System.Text;

namespace MonitorAndControl.Services;

public class WindowTracker : IDisposable
{
    private readonly Dictionary<string, string> _knownApps;
    private readonly Dictionary<string, AppTrackingPolicy> _trackingPolicies;
    private readonly object _sync = new();
    private readonly System.Threading.Timer _timer;
    private DateTime _lastPollErrorLogUtc = DateTime.MinValue;
    private bool _running;
    private IntPtr _pendingWindowHandle;
    private long _pendingSinceTick;
    private bool _pendingHadRecentInput;
    private IntPtr _lastIgnoredWindowHandle;

    private static readonly long OverlayFocusDelayMs = 1500;

    public string? CurrentAppName { get; private set; }
    public string? CurrentProcessName { get; private set; }
    public IntPtr CurrentWindowHandle { get; private set; }

    public event Action<string, string>? OnActiveWindowChanged;

    public IReadOnlyDictionary<string, string> KnownApps => _knownApps;

    public WindowTracker()
    {
        _knownApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _trackingPolicies = new Dictionary<string, AppTrackingPolicy>(StringComparer.OrdinalIgnoreCase);
        _timer = new System.Threading.Timer(Poll, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void LoadKnownApps(Dictionary<string, string> apps)
    {
        lock (_sync)
        {
            _knownApps.Clear();
            _trackingPolicies.Clear();
            foreach (var kvp in apps)
            {
                _knownApps[kvp.Key] = kvp.Value;
                _trackingPolicies[kvp.Key] = new AppTrackingPolicy();
            }
        }
    }

    public void AddKnownApp(string processName, string displayName,
        bool countInBackground = false, bool ignoreOverlayFocus = false)
    {
        lock (_sync)
        {
            _knownApps[processName] = displayName;
            _trackingPolicies[processName] = new AppTrackingPolicy(countInBackground, ignoreOverlayFocus);
        }
    }

    public string? GetProcessNameForApp(string appName)
    {
        lock (_sync)
            return _knownApps
                .Where(kvp => kvp.Value.Equals(appName, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .FirstOrDefault();
    }

    public void Start(int intervalMs = 1000)
    {
        _running = true;
        _timer.Change(0, intervalMs);
    }

    public void Stop()
    {
        _running = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void Poll(object? state)
    {
        if (!_running) return;
        try
        {
            var hWnd = NativeMethods.GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
                return;
            if (hWnd == CurrentWindowHandle)
            {
                _pendingWindowHandle = IntPtr.Zero;
                _lastIgnoredWindowHandle = IntPtr.Zero;
                return;
            }

            var title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
                return;

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return;

            var procName = GetProcessName(pid);
            if (procName == null) return;

            string appName;
            AppTrackingPolicy policy;
            lock (_sync)
            {
                appName = _knownApps.TryGetValue(procName, out var friendly)
                    ? friendly
                    : Path.GetFileNameWithoutExtension(procName);
                policy = _trackingPolicies.TryGetValue(procName, out var configured)
                    ? configured
                    : new AppTrackingPolicy();
            }

            if (policy.IgnoreOverlayFocus && CurrentWindowHandle != IntPtr.Zero)
            {
                if (!IsProcessMainWindow(pid, hWnd))
                {
                    if (_lastIgnoredWindowHandle != hWnd)
                        Logger.Instance.Info($"Ignored overlay focus: {appName} ({procName}) - \"{title}\"");
                    _lastIgnoredWindowHandle = hWnd;
                    _pendingWindowHandle = IntPtr.Zero;
                    return;
                }

                if (_pendingWindowHandle != hWnd)
                {
                    _pendingWindowHandle = hWnd;
                    _pendingSinceTick = Environment.TickCount64;
                    _pendingHadRecentInput = WasUserInputRecent();
                    return;
                }

                if (!_pendingHadRecentInput && WasUserInputRecent())
                {
                    _pendingHadRecentInput = true;
                    _pendingSinceTick = Environment.TickCount64;
                }

                if (!_pendingHadRecentInput || Environment.TickCount64 - _pendingSinceTick < OverlayFocusDelayMs)
                    return;
            }

            CurrentAppName = appName;
            CurrentProcessName = procName;
            CurrentWindowHandle = hWnd;
            _pendingWindowHandle = IntPtr.Zero;
            _lastIgnoredWindowHandle = IntPtr.Zero;

            Logger.Instance.Info($"Window: {appName} ({procName}) - \"{title}\"");
            OnActiveWindowChanged?.Invoke(appName, procName);
        }
        catch (Exception ex)
        {
            if ((DateTime.UtcNow - _lastPollErrorLogUtc).TotalSeconds >= 30)
            {
                _lastPollErrorLogUtc = DateTime.UtcNow;
                Logger.Instance.Error($"Window polling failed: {ex.Message}");
            }
        }
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? GetProcessName(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName + ".exe";
        }
        catch
        {
            return null;
        }
    }

    private static bool IsProcessMainWindow(uint pid, IntPtr hWnd)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            var mainWindow = proc.MainWindowHandle;
            return mainWindow == IntPtr.Zero || mainWindow == hWnd;
        }
        catch
        {
            return true;
        }
    }

    private static bool WasUserInputRecent()
    {
        var info = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
        };
        if (!NativeMethods.GetLastInputInfo(ref info))
            return true;

        var now = unchecked((uint)Environment.TickCount);
        return unchecked(now - info.dwTime) <= 2000;
    }

    public IReadOnlyList<(string AppName, string ProcessName)> GetRunningBackgroundApps()
    {
        List<(string ProcessName, string AppName)> configured;
        lock (_sync)
            configured = _trackingPolicies
                .Where(kvp => kvp.Value.CountInBackground && _knownApps.ContainsKey(kvp.Key))
                .Select(kvp => (kvp.Key, _knownApps[kvp.Key]))
                .ToList();

        var runningProcessNames = GetRunningProcessNameSnapshot();
        return configured
            .Where(x => runningProcessNames.Contains(NormalizeProcessName(x.ProcessName)))
            .Select(x => (x.AppName, x.ProcessName))
            .GroupBy(x => x.AppName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    public bool IsProcessRunning(string processName)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(processName);
            var processes = Process.GetProcessesByName(name);
            try { return processes.Length > 0; }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
        catch { return false; }
    }

    public string[] GetRunningProcessNames()
    {
        string[] processNames;
        lock (_sync)
            processNames = _knownApps.Keys.ToArray();
        var runningProcessNames = GetRunningProcessNameSnapshot();
        return processNames
            .Where(processName => runningProcessNames.Contains(NormalizeProcessName(processName)))
            .ToArray();
    }

    private static HashSet<string> GetRunningProcessNameSnapshot()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return running;
        }

        foreach (var process in processes)
        {
            try { running.Add(process.ProcessName); }
            catch { }
            finally { process.Dispose(); }
        }
        return running;
    }

    private static string NormalizeProcessName(string processName) =>
        Path.GetFileNameWithoutExtension(processName);

    public void Dispose()
    {
        _timer?.Dispose();
    }
}

public readonly record struct AppTrackingPolicy(
    bool CountInBackground = false,
    bool IgnoreOverlayFocus = false);
