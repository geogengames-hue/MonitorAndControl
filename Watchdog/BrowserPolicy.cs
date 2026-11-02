using System.Text.Json;
using Microsoft.Win32;
using MonitorAndControl.ScreenTime;

namespace MonitorAndControlWatchdog;

/// <summary>
/// Writes the "blocked websites" list that Chrome, Edge, Brave and Firefox read from Windows policy.
/// Only an administrator (here: the SYSTEM service) can write these keys, so the child cannot undo
/// them, and the browser shows its own "blocked by your administrator" page, also in private windows.
/// Entries that were already in a browser's list are left alone.
/// </summary>
internal sealed class BrowserPolicy
{
    private sealed class State
    {
        public List<string> Reported { get; set; } = new();
        public Dictionary<string, List<string>> Owned { get; set; } = new();
    }

    private static readonly (string Path, bool Firefox)[] Targets =
    {
        (@"SOFTWARE\Policies\Google\Chrome\URLBlocklist", false),
        (@"SOFTWARE\Policies\Microsoft\Edge\URLBlocklist", false),
        (@"SOFTWARE\Policies\BraveSoftware\Brave\URLBlocklist", false),
        (@"SOFTWARE\Policies\Mozilla\Firefox\WebsiteFilter\Block", true)
    };

    private readonly string _stateFile;
    private readonly Action<string> _log;
    private State _state;

    internal BrowserPolicy(string stateFile, Action<string> log)
    {
        _stateFile = stateFile;
        _log = log;
        _state = Load();
    }

    internal IReadOnlyList<string> Reported => _state.Reported;

    private State Load()
    {
        try { return File.Exists(_stateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(_stateFile)) ?? new() : new(); }
        catch { return new(); }
    }

    private void Save()
    {
        try { ScreenTimeFiles.WriteAtomic(_stateFile, _state); }
        catch (Exception ex) { _log("Site blocking: could not save state: " + ex.Message); }
    }

    /// <summary>Remembers the sites DeviceMon wants blocked. Returns true when the list changed.</summary>
    internal bool SetReported(List<string> hosts)
    {
        var next = hosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
        if (next.SequenceEqual(_state.Reported.OrderBy(h => h, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) return false;
        _state.Reported = next;
        Save();
        return true;
    }

    /// <summary>Applies reported plus always-blocked (sealed) sites to every browser's policy.</summary>
    internal void Apply(IEnumerable<string> sealedAlways)
    {
        var hosts = SiteBlocking.NormalizeAll(_state.Reported.Concat(sealedAlways));
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var (path, firefox) in Targets)
        {
            try
            {
                var desired = firefox ? SiteBlocking.FirefoxEntries(hosts) : SiteBlocking.ChromiumEntries(hosts);
                var current = ReadValues(machine, path);
                var previous = _state.Owned.GetValueOrDefault(path) ?? new List<string>();
                var final = SiteBlocking.Merge(current, previous, desired);
                if (!final.SequenceEqual(current, StringComparer.OrdinalIgnoreCase)) Write(machine, path, final);
                _state.Owned[path] = desired;
            }
            catch (Exception ex) { _log($"Site blocking: could not update {path}: {ex.Message}"); }
        }
        Save();
    }

    private static List<string> ReadValues(RegistryKey machine, string path)
    {
        using var key = machine.OpenSubKey(path);
        if (key == null) return new();
        return key.GetValueNames()
            .OrderBy(n => int.TryParse(n, out var i) ? i : int.MaxValue).ThenBy(n => n, StringComparer.Ordinal)
            .Select(n => key.GetValue(n) as string)
            .Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList();
    }

    private static void Write(RegistryKey machine, string path, List<string> entries)
    {
        if (entries.Count == 0) { machine.DeleteSubKeyTree(path, throwOnMissingSubKey: false); return; }
        using var key = machine.CreateSubKey(path, writable: true)!;
        var keep = new HashSet<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            var name = (i + 1).ToString();
            key.SetValue(name, entries[i], RegistryValueKind.String);
            keep.Add(name);
        }
        foreach (var stale in key.GetValueNames().Where(n => !keep.Contains(n))) key.DeleteValue(stale, throwOnMissingValue: false);
    }
}
