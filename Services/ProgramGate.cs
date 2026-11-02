using System.Collections.Concurrent;
using System.Diagnostics;
using MonitorAndControl.Data;
using MonitorAndControl.ScreenTime;

namespace MonitorAndControl.Services;

/// <summary>
/// "New programs need my OK": when the parent turns it on, the programs this account already has are recorded as
/// allowed. A program opened after that is closed and the parent is asked; it opens again once they allow it.
/// The list is kept by the SYSTEM service, so nothing here can allow a program; this only closes and asks.
/// Programs are known by the original file name built into them, so renaming one does not get it past.
/// </summary>
public sealed class ProgramGate : IDisposable
{
    private static readonly TimeSpan ExemptRecheck = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TellAgain = TimeSpan.FromMinutes(1);

    // Apps that come with Windows but live with the Store apps (C:\Program Files\WindowsApps), by package name.
    private static readonly string[] InboxPackages =
    {
        "Microsoft.WindowsNotepad_", "Microsoft.WindowsCalculator_", "Microsoft.Windows.Photos_", "Microsoft.WindowsTerminal_",
        "Microsoft.ScreenSketch_", "Microsoft.Paint_", "Microsoft.WindowsCamera_", "Microsoft.WindowsAlarms_",
        "Microsoft.WindowsSoundRecorder_", "Microsoft.MicrosoftStickyNotes_", "Microsoft.WindowsStore_", "Microsoft.ZuneMusic_",
        "Microsoft.ZuneVideo_", "Microsoft.Todos_", "Microsoft.OutlookForWindows_", "Microsoft.WindowsFeedbackHub_",
        "Microsoft.GetHelp_", "Microsoft.Getstarted_", "Microsoft.SecHealthUI_", "Microsoft.MicrosoftOfficeHub_",
        "Microsoft.PowerAutomateDesktop_", "Microsoft.BingWeather_", "Microsoft.BingNews_", "MicrosoftWindows.Client.",
        "MicrosoftCorporationII.QuickAssist_", "Microsoft.WindowsMaps_", "Microsoft.People_", "Microsoft.Windows.DevHome_"
    };

    private readonly UsageDatabase _db;
    private readonly WindowTracker _tracker;
    private readonly LimitEnforcer _enforcer;
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _told = new();
    private readonly string _windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private readonly string _storeApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
    private readonly string _ownFolder = AppContext.BaseDirectory;
    private readonly string _popupFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemHelper");
    // Administrator accounts (the parent's) are never held up; the service also says so on the first request.
    private DateTime _exemptUntil;
    private volatile Snapshot _snapshot = new(new ProgramsState(), new Dictionary<string, ProgramEntry>(), DateTime.MinValue);
    private DateTime _checkedAt;

    private sealed record Snapshot(ProgramsState State, Dictionary<string, ProgramEntry> Index, DateTime WrittenUtc);

    public ProgramGate(UsageDatabase db, WindowTracker tracker, LimitEnforcer enforcer)
    {
        _db = db;
        _tracker = tracker;
        _enforcer = enforcer;
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        if (AccountIdentity.IsAdministrator) return;
        _tracker.OnActiveWindowChanged += (app, proc) => _ = CheckAsync(proc);
        _timer.Change(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5));
    }

    // The service's list, read again only when the file changed.
    private Snapshot Current(bool force = false)
    {
        if (!force && DateTime.UtcNow - _checkedAt < TimeSpan.FromSeconds(2)) return _snapshot;
        _checkedAt = DateTime.UtcNow;
        DateTime written;
        try { written = File.Exists(ProgramApprovals.Path) ? File.GetLastWriteTimeUtc(ProgramApprovals.Path) : DateTime.MinValue; }
        catch { return _snapshot; }
        if (!force && written == _snapshot.WrittenUtc) return _snapshot;
        var state = ProgramApprovals.Read();
        var index = new Dictionary<string, ProgramEntry>();
        foreach (var entry in state.Entries) index.TryAdd(entry.Name, entry);
        return _snapshot = new Snapshot(state, index, written);
    }

    // Every few seconds: record what this account already has (once per switch-on), and look at the program in front again,
    // in case it was opened while an answer was on its way.
    private async Task TickAsync()
    {
        var current = Current();
        if (!current.State.Enabled || DateTime.UtcNow < _exemptUntil) return;
        if (!current.State.Baselined.Contains(AccountIdentity.CurrentSid)) { await BaselineAsync(current.State.EnabledUtc); return; }
        if (_tracker.CurrentProcessName is { } process) await CheckAsync(process);
    }

    private async Task BaselineAsync(DateTimeOffset? enabledUtc)
    {
        if (!await _busy.WaitAsync(0)) return;
        try
        {
            var names = await Task.Run(() =>
            {
                // A program file created after the switch-on is not "already there", even if this account is
                // only now signing in for the first time since.
                bool Older(string? path)
                {
                    try { return enabledUtc == null || string.IsNullOrEmpty(path) || !File.Exists(path) || File.GetCreationTimeUtc(path) <= enabledUtc.Value.UtcDateTime; }
                    catch { return true; }
                }
                var list = new List<string?>(_tracker.KnownApps.Keys.Select(ProgramApprovals.Key));
                try
                {
                    foreach (var app in new DiscoveryService().ScanForApps().Where(a => Older(a.InstallPath)))
                        list.Add(IdentityOf(app.ProcessName, app.InstallPath));
                }
                catch (Exception ex) { Logger.Instance.Warn("Program scan for the allowed list failed: " + ex.Message); }
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (process.SessionId != AccountIdentity.SessionId) continue;
                        var path = process.MainModule?.FileName;
                        if (Older(path)) list.Add(IdentityOf(process.ProcessName, path));
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
                return list;
            });
            names.AddRange((await _db.GetAppMappingsAsync()).Select(m => ProgramApprovals.Key(m.ProcessName)));
            var reply = await ScreenTimeClient.SendAsync(new ScreenTimeRequest
            {
                Command = "program-baseline",
                ProgramNames = names.Where(k => k != null).Select(k => k!).Distinct().Take(ProgramApprovals.MaxBaseline).ToList()
            }, CancellationToken.None);
            if (reply.Ok && Field(reply, "state") == "exempt") _exemptUntil = DateTime.UtcNow + ExemptRecheck;
            else if (reply.Ok) Logger.Instance.Info("Programs already on this PC were recorded as allowed.");
            Current(force: true);
        }
        catch (Exception ex) { Logger.Instance.Warn("Could not record the allowed programs yet: " + ex.Message); }
        finally { _busy.Release(); }
    }

    private async Task CheckAsync(string processName)
    {
        try
        {
            var current = Current();
            if (!current.State.Enabled || _enforcer.IsPaused || DateTime.UtcNow < _exemptUntil) return;
            var processKey = ProgramApprovals.Key(processName);
            if (processKey == null) return;
            var path = ProgramPath(processName);
            // Windows itself, its built-in apps and DeviceMon are never "new"; a program whose file cannot be read is left alone.
            if (path == null || IsUnder(path, _windows) || IsUnder(path, _ownFolder) || IsUnder(path, _popupFolder) || IsInboxApp(path)) return;
            var key = IdentityOf(processName, path);
            if (key == null || current.Index.GetValueOrDefault(key)?.State == ProgramApprovals.Allowed) return;
            // An always-allowed app counts only under its own name (a renamed program is not it).
            var appName = _tracker.KnownApps.TryGetValue(processName, out var friendly) ? friendly : Path.GetFileNameWithoutExtension(processName);
            if (key == processKey && _enforcer.IsAlwaysAllowed(appName)) return;

            if (!await _busy.WaitAsync(0)) return;
            try
            {
                var label = Describe(path);
                var reply = await ScreenTimeClient.SendAsync(new ScreenTimeRequest
                {
                    Command = "program-request", ProgramName = key, ProgramDisplay = label, AccountName = Environment.UserName
                }, CancellationToken.None);
                if (!reply.Ok) { Logger.Instance.Warn("New program check: " + reply.Error); return; }
                var answer = Field(reply, "state");
                if (answer == "exempt") { _exemptUntil = DateTime.UtcNow + ExemptRecheck; return; }
                if (answer is not (ProgramApprovals.Waiting or ProgramApprovals.Blocked)) { Current(force: true); return; }

                Logger.Instance.Warn($"New program {key} is not allowed yet ({answer}); closing it.");
                await Task.Run(() => LimitEnforcer.KillProcessByName(processName));
                var isNew = reply.Data is { } data && data.TryGetProperty("isNew", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.True;
                if (isNew || !_told.TryGetValue(key, out var last) || DateTime.UtcNow - last > TellAgain)
                {
                    _told[key] = DateTime.UtcNow;
                    ChildNotice.Show(answer == ProgramApprovals.Blocked
                        ? $"Your parent has not allowed {label} on this PC."
                        : isNew ? $"{label} is new on this PC, so your parent has to allow it first. They have been asked."
                                : $"{label} is still waiting for your parent's OK.");
                }
            }
            finally { _busy.Release(); }
        }
        catch (Exception ex) { Logger.Instance.Warn("New program check skipped: " + ex.Message); }
    }

    private static string? IdentityOf(string processName, string? path)
    {
        string? original = null;
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) original = FileVersionInfo.GetVersionInfo(path).OriginalFilename; }
        catch { }
        return ProgramApprovals.Identity(processName, original);
    }

    private static string? Field(ScreenTimeReply reply, string name) =>
        reply.Data is { } data && data.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;

    private static string? ProgramPath(string processName)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName)))
        {
            try { if (process.SessionId == AccountIdentity.SessionId && process.MainModule?.FileName is { Length: > 0 } file) return file; }
            catch { }
            finally { process.Dispose(); }
        }
        return null;
    }

    private bool IsInboxApp(string path)
    {
        if (!IsUnder(path, _storeApps)) return false;
        var package = Path.GetRelativePath(_storeApps, path).Split(Path.DirectorySeparatorChar)[0];
        return InboxPackages.Any(p => package.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnder(string path, string folder) =>
        folder.Length > 0 && Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The program's own description ("Minecraft Launcher"), or its file name.</summary>
    private static string Describe(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var text = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>A short message to the child, on top of other windows, without holding up the caller.</summary>
internal static class ChildNotice
{
    public static void Show(string text) => Ask(text, yesNo: false, _ => { });

    /// <summary>Shows a question; <paramref name="answered"/> gets true for Yes (or OK).</summary>
    public static void Ask(string text, bool yesNo, Action<bool> answered)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var result = MessageBox.Show(text, "Monitor & Control", yesNo ? MessageBoxButtons.YesNo : MessageBoxButtons.OK,
                    MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
                answered(result is DialogResult.Yes or DialogResult.OK);
            }
            catch (Exception ex)
            {
                Logger.Instance.Warn("Could not show a message: " + ex.Message);
                answered(false);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
