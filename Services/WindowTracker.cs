using System.Diagnostics;
using System.Text;

namespace MonitorAndControl.Services;

public class WindowTracker : IDisposable
{
    private readonly Dictionary<string, string> _knownApps;
    private readonly System.Threading.Timer _timer;
    private bool _running;

    public string? CurrentAppName { get; private set; }
    public string? CurrentProcessName { get; private set; }
    public IntPtr CurrentWindowHandle { get; private set; }

    public event Action<string, string>? OnActiveWindowChanged;

    public IReadOnlyDictionary<string, string> KnownApps => _knownApps;

    public WindowTracker()
    {
        _knownApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _timer = new System.Threading.Timer(Poll, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void LoadKnownApps(Dictionary<string, string> apps)
    {
        _knownApps.Clear();
        foreach (var kvp in apps)
            _knownApps[kvp.Key] = kvp.Value;
    }

    public void AddKnownApp(string processName, string displayName)
    {
        _knownApps[processName] = displayName;
    }

    public string? GetProcessNameForApp(string appName)
    {
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
            if (hWnd == IntPtr.Zero || hWnd == CurrentWindowHandle)
                return;

            var title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
                return;

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return;

            var procName = GetProcessName(pid);
            if (procName == null) return;

            var appName = _knownApps.TryGetValue(procName, out var friendly)
                ? friendly
                : Path.GetFileNameWithoutExtension(procName);

            CurrentAppName = appName;
            CurrentProcessName = procName;
            CurrentWindowHandle = hWnd;

            Logger.Instance.Info($"Window: {appName} ({procName}) — \"{title}\"");
            OnActiveWindowChanged?.Invoke(appName, procName);
        }
        catch
        {
            // Silently ignore polling errors
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

    public bool IsProcessRunning(string processName)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(processName);
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch { return false; }
    }

    public string[] GetRunningProcessNames()
    {
        return _knownApps.Keys
            .Where(IsProcessRunning)
            .ToArray();
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
