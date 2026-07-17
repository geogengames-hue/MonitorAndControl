using System.Collections.Concurrent;

namespace MonitorAndControl.Services;

public class Logger
{
    public static Logger Instance { get; } = new();

    private readonly string _logPath;
    private readonly ConcurrentQueue<string> _recent = new();
    private const int MaxRecent = 1000;
    private readonly object _lock = new();

    private Logger()
    {
        // Stored in the SYSTEM-protected data directory (see AppPaths) so the log
        // cannot be deleted by a standard user once the watchdog has hardened it.
        _logPath = AppPaths.LogPath;
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private const long MaxFileSize = 2 * 1024 * 1024; // 2 MB (~1 week)
    private const int KeepLines = 15000;

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
                TrimIfOversized();
            }
            catch { }
        }
        _recent.Enqueue(line);
        if (_recent.Count > MaxRecent)
            _recent.TryDequeue(out _);
    }

    private void TrimIfOversized()
    {
        var fi = new FileInfo(_logPath);
        if (!fi.Exists || fi.Length <= MaxFileSize) return;

        var allLines = File.ReadAllLines(_logPath);
        if (allLines.Length <= KeepLines) return;

        var kept = allLines[^KeepLines..];
        File.WriteAllLines(_logPath, kept);
    }

    public string[] GetRecent(int count = 200)
    {
        try
        {
            if (!File.Exists(_logPath)) return [];
            return File.ReadAllLines(_logPath).Reverse().Take(count).Reverse().ToArray();
        }
        catch { return []; }
    }

    public string[] GetAll()
    {
        try
        {
            if (!File.Exists(_logPath)) return [];
            return File.ReadAllLines(_logPath);
        }
        catch { return []; }
    }

    public void Clear()
    {
        lock (_lock)
        {
            // Truncate rather than delete: the watchdog denies Delete on this file
            // to standard users, but writing/truncating is still permitted.
            try { File.WriteAllText(_logPath, string.Empty); } catch { }
            _recent.Clear();
        }
    }
}
