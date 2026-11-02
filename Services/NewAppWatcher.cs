using System.Text.Json;
using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

/// <summary>
/// Looks for newly installed programs and games every few hours and tells the parent, so a new app is
/// known about before it is ever used. The first scan only records what is already installed.
/// </summary>
public sealed class NewAppWatcher : IDisposable
{
    private const string KnownKeyPrefix = "KnownInstalledApps:";
    private const string AlertedKey = "AlertedInstalledApps";
    private const int MaxListed = 8;

    private readonly UsageDatabase _db;
    private readonly Func<string, Task<bool>> _alert;
    private readonly System.Threading.Timer _timer;
    private int _running;

    public NewAppWatcher(UsageDatabase db, Func<string, Task<bool>> alert)
    {
        _db = db;
        _alert = alert;
        _timer = new System.Threading.Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.FromMinutes(3), TimeSpan.FromHours(6));

    /// <summary>The apps in <paramref name="installed"/> that were not seen before (by process name).</summary>
    public static List<(string Key, string Name)> FindNew(IEnumerable<(string Key, string Name)> installed, IEnumerable<string> known)
    {
        var seen = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        return installed.Where(a => !string.IsNullOrWhiteSpace(a.Key) && !seen.Contains(a.Key))
            .GroupBy(a => a.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    public static string Describe(IReadOnlyList<(string Key, string Name)> added)
    {
        var names = added.Select(a => string.IsNullOrWhiteSpace(a.Name) ? a.Key : a.Name).Take(MaxListed).ToList();
        var more = added.Count > MaxListed ? $" and {added.Count - MaxListed} more" : "";
        return $"New program{(added.Count == 1 ? "" : "s")} installed: {string.Join(", ", names)}{more}. Open Limits to add a limit or block it.";
    }

    private async void Tick(object? state)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            // Every signed-in account scans what it can see (per-user installs are only visible to that account); the claim
            // only stops the same account scanning twice in a row, and the shared list keeps each program to one alert.
            if (!await _db.TryClaimLeaseAsync("NewAppScan:" + AccountIdentity.CurrentSid, TimeSpan.FromMinutes(20))) return;
            // Only what is installed: programs that were merely open (the "Running" source) are not new installs.
            var installed = await Task.Run(() => new DiscoveryService().ScanForApps()
                .Where(a => a.Source != "Running")
                .Select(a => (Key: a.ProcessName, Name: a.DisplayName)).ToList());
            var knownKey = KnownKeyPrefix + AccountIdentity.CurrentSid;
            var knownText = await _db.GetSettingAsync(knownKey, "");
            var known = new List<string>();
            try { if (knownText.Length > 0) known = JsonSerializer.Deserialize<List<string>>(knownText) ?? new(); } catch { }

            if (known.Count == 0)
            {
                // The first scan only records what is installed. An empty scan is not a baseline (it would make
                // every program look new next time).
                if (installed.Count > 0)
                    await _db.SetSettingAsync(knownKey, JsonSerializer.Serialize(installed.Select(a => a.Key).Distinct(StringComparer.OrdinalIgnoreCase)));
                return;
            }

            var added = FindNew(installed, known);
            if (added.Count == 0) return;

            // Programs another account's DeviceMon already told the parent about are not announced twice.
            var alerted = new List<string>();
            try { alerted = JsonSerializer.Deserialize<List<string>>(await _db.GetSettingAsync(AlertedKey, "[]")) ?? new(); } catch { }
            var toTell = FindNew(added, alerted);
            if (toTell.Count > 0 && await _db.GetSettingAsync("NewAppAlertsEnabled", "true") == "true")
                if (!await _alert($"[{Environment.UserName}] " + Describe(toTell))) return; // retry next time
            alerted.AddRange(toTell.Select(a => a.Key));
            await _db.SetSettingAsync(AlertedKey, JsonSerializer.Serialize(alerted.Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(500)));
            known.AddRange(added.Select(a => a.Key));
            await _db.SetSettingAsync(knownKey, JsonSerializer.Serialize(known.Distinct(StringComparer.OrdinalIgnoreCase)));
        }
        catch (Exception ex) { Logger.Instance.Warn($"New-app check skipped: {ex.Message}"); }
        finally { Volatile.Write(ref _running, 0); }
    }

    public void Dispose() => _timer.Dispose();
}
