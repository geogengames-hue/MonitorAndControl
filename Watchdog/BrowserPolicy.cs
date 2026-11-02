using System.Text.Json;
using Microsoft.Win32;
using MonitorAndControl.ScreenTime;

namespace MonitorAndControlWatchdog;

/// <summary>
/// Writes the browser rules that Chrome, Edge, Brave and Firefox read from Windows policy: blocked
/// websites, an optional "only these sites" list, and forced SafeSearch / YouTube Restricted Mode.
/// Only an administrator (here: the SYSTEM service) can write these keys, so the child cannot undo
/// them, and the browser shows its own "blocked by your administrator" page, also in private windows.
/// Entries that were already in a browser's lists are left alone.
/// </summary>
internal sealed class BrowserPolicy
{
    private sealed class State
    {
        public List<string> Reported { get; set; } = new();
        public List<string> ReportedAllowed { get; set; } = new();
        public bool ReportedAllowOnly { get; set; }
        public bool ReportedSafe { get; set; }
        /// <summary>Value each forced setting had before we set it (null = it did not exist), so turning it off restores it.</summary>
        public Dictionary<string, int?> SafeOriginals { get; set; } = new();
        public Dictionary<string, List<string>> Owned { get; set; } = new();
    }

    private sealed record Target(string Root, string BlockKey, string AllowKey, bool Firefox, string InternalPages,
        (string Name, int Value)[] Safe);

    private static readonly Target[] Targets =
    {
        new(@"SOFTWARE\Policies\Google\Chrome", "URLBlocklist", "URLAllowlist", false, "chrome://*",
            new[] { ("ForceGoogleSafeSearch", 1), ("ForceYouTubeRestrict", 2) }),
        new(@"SOFTWARE\Policies\Microsoft\Edge", "URLBlocklist", "URLAllowlist", false, "edge://*",
            new[] { ("ForceGoogleSafeSearch", 1), ("ForceBingSafeSearch", 2), ("ForceYouTubeRestrict", 2) }),
        new(@"SOFTWARE\Policies\BraveSoftware\Brave", "URLBlocklist", "URLAllowlist", false, "brave://*",
            new[] { ("ForceGoogleSafeSearch", 1), ("ForceYouTubeRestrict", 2) }),
        new(@"SOFTWARE\Policies\Mozilla\Firefox\WebsiteFilter", "Block", "Exceptions", true, "",
            Array.Empty<(string, int)>())
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

    /// <summary>Remembers what DeviceMon wants. Returns true when anything changed.</summary>
    internal bool SetReported(List<string> blocked, List<string> allowed, bool allowOnly, bool safe)
    {
        static List<string> Sorted(IEnumerable<string> x) => x.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
        var b = Sorted(blocked);
        var a = Sorted(allowed);
        var same = b.SequenceEqual(Sorted(_state.Reported), StringComparer.OrdinalIgnoreCase)
            && a.SequenceEqual(Sorted(_state.ReportedAllowed), StringComparer.OrdinalIgnoreCase)
            && allowOnly == _state.ReportedAllowOnly && safe == _state.ReportedSafe;
        if (same) return false;
        _state.Reported = b;
        _state.ReportedAllowed = a;
        _state.ReportedAllowOnly = allowOnly;
        _state.ReportedSafe = safe;
        Save();
        return true;
    }

    /// <summary>Combines what DeviceMon reported with what the parent sealed, then writes every browser's policy.</summary>
    internal void Apply(SealedLimits? sealedContent, bool homework = false)
    {
        var plan = SiteBlocking.ComputePlan(_state.Reported, _state.ReportedAllowed, _state.ReportedAllowOnly,
            _state.ReportedSafe, sealedContent, homework);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var target in Targets)
        {
            // Each part has its own try so one failure cannot stop the others, and a failed
            // safe-browsing change is retried on the next pass instead of being forgotten.
            try { ApplyList(machine, target, target.BlockKey, BlockEntries(target, plan)); }
            catch (Exception ex) { _log($"Site blocking: could not update the block list in {target.Root}: {ex.Message}"); }
            try { ApplyList(machine, target, target.AllowKey, AllowEntries(target, plan)); }
            catch (Exception ex) { _log($"Site blocking: could not update the allow list in {target.Root}: {ex.Message}"); }
            try { ApplySafe(machine, target, plan.SafeBrowsing); }
            catch (Exception ex) { _log($"Site blocking: could not update safe browsing in {target.Root}: {ex.Message}"); }
        }
        Save();
    }

    private static List<string> BlockEntries(Target target, PolicyPlan plan)
    {
        var entries = target.Firefox ? SiteBlocking.FirefoxEntries(plan.BlockHosts) : SiteBlocking.ChromiumEntries(plan.BlockHosts);
        if (plan.AllowOnly) entries.Insert(0, target.Firefox ? "<all_urls>" : "*");
        return entries;
    }

    private static List<string> AllowEntries(Target target, PolicyPlan plan)
    {
        if (!plan.AllowOnly) return new();
        // The dashboard on this PC and the browser's own pages must keep working.
        var local = new[] { "localhost", "127.0.0.1", "[::1]", Environment.MachineName.ToLowerInvariant() };
        if (target.Firefox) return SiteBlocking.FirefoxEntries(plan.AllowHosts.Concat(local)).ToList();
        return plan.AllowHosts.Concat(local).Concat(new[] { target.InternalPages, "about:*" }).ToList();
    }

    private void ApplyList(RegistryKey machine, Target target, string listName, List<string> desired)
    {
        var path = target.Root + "\\" + listName;
        var current = ReadValues(machine, path);
        var previous = _state.Owned.GetValueOrDefault(path) ?? new List<string>();
        var final = SiteBlocking.Merge(current, previous, desired);
        if (!final.SequenceEqual(current, StringComparer.OrdinalIgnoreCase)) Write(machine, path, final);
        _state.Owned[path] = desired;
    }

    private void ApplySafe(RegistryKey machine, Target target, bool on)
    {
        if (target.Safe.Length == 0) return;
        if (on)
        {
            using var key = machine.CreateSubKey(target.Root, writable: true)!;
            foreach (var (name, value) in target.Safe)
            {
                var id = target.Root + "|" + name;
                if (!_state.SafeOriginals.ContainsKey(id)) _state.SafeOriginals[id] = key.GetValue(name) is int before ? before : null;
                key.SetValue(name, value, RegistryValueKind.DWord);
            }
            return;
        }

        // Off: put back what was there before (or remove ours). An entry is forgotten only after it worked.
        foreach (var id in _state.SafeOriginals.Keys.Where(k => k.StartsWith(target.Root + "|", StringComparison.Ordinal)).ToList())
        {
            var name = id[(target.Root.Length + 1)..];
            using var key = machine.OpenSubKey(target.Root, writable: true);
            if (key != null)
            {
                if (_state.SafeOriginals[id] is int original) key.SetValue(name, original, RegistryValueKind.DWord);
                else key.DeleteValue(name, throwOnMissingValue: false);
            }
            _state.SafeOriginals.Remove(id);
        }
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
