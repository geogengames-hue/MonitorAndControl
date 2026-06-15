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
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SystemHelper");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "events.log");
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (_lock)
        {
            try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
        }
        _recent.Enqueue(line);
        if (_recent.Count > MaxRecent)
            _recent.TryDequeue(out _);
    }

    public string[] GetRecent(int count = 200) => _recent.Reverse().Take(count).Reverse().ToArray();

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
            try { File.Delete(_logPath); } catch { }
            _recent.Clear();
        }
    }
}
