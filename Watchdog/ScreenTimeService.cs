using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using MonitorAndControl.ScreenTime;

namespace MonitorAndControlWatchdog;

internal sealed class ScreenTimeService : IDisposable
{
    private readonly object _sync = new();
    private readonly Action<string> _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, HashSet<int>> _warnings = new();
    private readonly Dictionary<int, long> _logoffs = new();
    private readonly Dictionary<string, ScreenTimeUsage> _usage;
    private System.Threading.Timer? _timer;
    private long _lastTick;
    private long _lastPersist;
    private long _lastStatus;
    private string _lastError = "";
    private ScreenTimeControlServer? _control;

    internal static void PrepareDirectory()
    {
        var root = new DirectoryInfo(ScreenTimeFiles.DirectoryPath);
        if (root.Exists)
        {
            if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Screen-time directory must not be a reparse point.");
            var owner = root.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) ?? throw new UnauthorizedAccessException("Missing directory owner.");
            using var identity = WindowsIdentity.GetCurrent();
            if (owner.Value != "S-1-5-18" && owner.Value != "S-1-5-32-544" && owner.Value != identity.User?.Value)
                throw new UnauthorizedAccessException("Screen-time directory has an untrusted owner. Administrative recovery required.");
        }
        root.Create();
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(true, false);
        foreach (var type in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(type, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        root.SetAccessControl(security);
        foreach (var file in root.GetFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Screen-time files must not be reparse points.");
            var fileOwner = file.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) ?? throw new UnauthorizedAccessException("Missing file owner.");
            using var identity = WindowsIdentity.GetCurrent();
            if (fileOwner.Value != "S-1-5-18" && fileOwner.Value != "S-1-5-32-544" && fileOwner.Value != identity.User?.Value)
                throw new UnauthorizedAccessException("Untrusted screen-time file owner: " + file.Name);
            var acl = new FileSecurity();
            acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            acl.SetAccessRuleProtection(true, false);
            foreach (var type in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(type, null), FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            file.SetAccessControl(acl);
        }
    }

    internal ScreenTimeService(Action<string> log)
    {
        _log = log;
        PrepareDirectory();
        _usage = ReadUsage();
        _lastTick = _clock.ElapsedMilliseconds;
        try { _control = new ScreenTimeControlServer(HandleControl, _log); }
        catch { _log("Screen-time configuration channel unavailable; enforcement continues."); }
        _timer = new System.Threading.Timer(Tick, null, 1000, 1000);
    }

    private Dictionary<string, ScreenTimeUsage> ReadUsage()
    {
        if (!File.Exists(ScreenTimeFiles.UsagePath)) return new();
        foreach (var path in new[] { ScreenTimeFiles.UsagePath, ScreenTimeFiles.UsagePath + ".bak" })
        {
            try
            {
                var result = JsonSerializer.Deserialize<Dictionary<string, ScreenTimeUsage>>(File.ReadAllText(path))
                    ?? throw new InvalidDataException();
                if (result.Values.Any(u => !double.IsFinite(u.Seconds) || u.Seconds < 0
                    || !DateOnly.TryParseExact(u.Day, "yyyy-MM-dd", out _)))
                    throw new InvalidDataException();
                if (path.EndsWith(".bak")) _log("Screen-time: recovered usage backup.");
                return result;
            }
            catch (Exception ex) { _log("Screen-time usage load: " + ex.Message); }
        }
        throw new InvalidDataException("Cannot recover screen-time counters; administrative repair required.");
    }

    private void Tick(object? state)
    {
        if (!Monitor.TryEnter(_sync)) return;
        try
        {
            var tick = _clock.ElapsedMilliseconds;
            var elapsed = (tick - _lastTick) / 1000.0;
            _lastTick = tick;
            // Sleep/resume and scheduler stalls are not charged as active usage.
            if (elapsed > 5) elapsed = 0;
            var today = DateOnly.FromDateTime(DateTime.Now);
            var policies = ScreenTimeFiles.ReadPolicies();
            var sessions = policies.Any(p => p.Enabled) ? ScreenTimeWindows.Sessions() : new();
            foreach (var policy in policies)
            {
                if (!_usage.TryGetValue(policy.ProfileId, out var usage))
                    _usage[policy.ProfileId] = usage = new();
                var userSessions = sessions.Where(s => s.Sid == policy.UserSid).ToList();
                ScreenTimeRules.Advance(usage, today,
                    policy.Enabled && userSessions.Any(s => s.Unlocked) ? elapsed : 0);
            }
            // Checkpoint periodically (and always right before a logoff) rather than every
            // second: a restart loses at most a few seconds of usage, without constant disk churn.
            PersistUsage(tick, force: false);
            foreach (var policy in policies.Where(p => p.Enabled))
            {
                var usage = _usage[policy.ProfileId];
                var remaining = ScreenTimeRules.Remaining(policy, usage, today);
                var warningKey = policy.ProfileId + ":" + usage.Day;
                if (!_warnings.TryGetValue(warningKey, out var sent)) _warnings[warningKey] = sent = new();
                foreach (var session in sessions.Where(s => s.Sid == policy.UserSid))
                {
                    if (remaining == 0)
                    {
                        if (!_logoffs.TryGetValue(session.SessionId, out var last) || tick - last >= 5000)
                        {
                            PersistUsage(tick, force: true);
                            ScreenTimeWindows.Logoff(session.SessionId, policy.UserSid);
                            _logoffs[session.SessionId] = tick;
                            _log("Screen-time: quota exhausted; logged off " + policy.UserSid);
                        }
                    }
                    else if (session.Unlocked)
                    {
                        var threshold = new[] { 1, 5, 10 }.FirstOrDefault(m => remaining <= m * 60);
                        if (threshold > 0 && !sent.Contains(threshold))
                        {
                            ScreenTimeWindows.Warn(session.SessionId,
                                $"About {Math.Ceiling(remaining / 60.0)} minute(s) of PC time left today. Save your work - you will be signed out when time runs out.");
                            sent.Add(threshold);
                        }
                    }
                }
            }
            if (tick - _lastStatus >= 5000)
            {
                WriteStatus(policies, today, null);
                _lastStatus = tick;
            }
            _lastError = "";
        }
        catch (Exception ex)
        {
            if (_lastError != ex.Message) { _log("Screen-time error: " + ex); _lastError = ex.Message; }
            try { WriteStatus(new(), DateOnly.FromDateTime(DateTime.Now), ex.Message); } catch { }
        }
        finally { Monitor.Exit(_sync); }
    }

    private void PersistUsage(long tick, bool force)
    {
        if (!force && tick - _lastPersist < 10000) return;
        ScreenTimeFiles.WriteAtomic(ScreenTimeFiles.UsagePath, _usage);
        _lastPersist = tick;
    }

    private void WriteStatus(List<ScreenTimePolicy> policies, DateOnly today, string? error)
    {
        ScreenTimeFiles.WriteAtomic(ScreenTimeFiles.StatusPath, new {
            updatedUtc = DateTimeOffset.UtcNow, error,
            profiles = policies.Select(p => {
                var usage = _usage.GetValueOrDefault(p.ProfileId) ?? new ScreenTimeUsage();
                return new {
                    profileId = p.ProfileId, userSid = p.UserSid, name = p.DisplayName,
                    dailyMinutes = p.DailyMinutes, enabled = p.Enabled,
                    usedSeconds = (long)Math.Ceiling(usage.Seconds),
                    remainingSeconds = ScreenTimeRules.Remaining(p, usage, today), day = usage.Day
                };
            }).ToList()
        });
    }


    private ScreenTimeReply HandleControl(ScreenTimeRequest request)
    {
        List<ScreenTimeWindows.Account> accounts;
        try { accounts = ScreenTimeWindows.Accounts(includeExcluded: true); }
        catch { return ScreenTimeReply.Failure("Could not list local accounts."); }
        lock (_sync)
        {
            try
            {
                var policies = ScreenTimeFiles.ReadPolicies();

                if (request.Command is "save" or "validate")
                {
                    var account = accounts.FirstOrDefault(a => a.Sid == request.UserSid);
                    var existing = policies.FirstOrDefault(p => p.UserSid == request.UserSid);
                    if ((account == null || !account.Eligible) && (request.Enabled || existing == null))
                        return ScreenTimeReply.Failure("Choose an enabled standard account. Administrators cannot be controlled.");
                    if (account != null && !account.Eligible && existing != null && !request.Enabled)
                    {
                        // Only disabling an old rule is allowed after a promotion/removal.
                    }
                    var policy = existing ?? new ScreenTimePolicy { UserSid = request.UserSid };
                    policy.DisplayName = account?.Name ?? policy.DisplayName;
                    policy.DailyMinutes = request.DailyMinutes;
                    policy.Enabled = request.Enabled;
                    policy.BonusDay = DateTime.Today.ToString("yyyy-MM-dd");
                    policy.BonusMinutes = request.BonusMinutes;
                    policy.Validate();
                    if (request.Command == "validate") return ScreenTimeReply.Success(new { valid = true });
                    if (existing == null) policies.Add(policy);
                    ScreenTimeFiles.WriteAtomic(ScreenTimeFiles.PoliciesPath, policies);
                    File.AppendAllText(Path.Combine(ScreenTimeFiles.DirectoryPath, "admin-audit.log"),
                        $"{DateTimeOffset.Now:o} Windows admin authorized policy for {policy.UserSid}; minutes={policy.DailyMinutes}; enabled={policy.Enabled}; bonus={policy.BonusMinutes}{Environment.NewLine}");
                    return ScreenTimeReply.Success(new { saved = true, userSid = policy.UserSid });
                }
                foreach (var policy in policies)
                    if (!accounts.Any(a => a.Sid == policy.UserSid))
                        accounts.Add(new ScreenTimeWindows.Account(policy.DisplayName, policy.UserSid, false, "Account not found"));
                var today = DateOnly.FromDateTime(DateTime.Now);
                return ScreenTimeReply.Success(new {
                    accounts,
                    profiles = policies.Select(p => {
                        var usage = _usage.GetValueOrDefault(p.ProfileId) ?? new ScreenTimeUsage();
                        return new {
                            profileId = p.ProfileId, userSid = p.UserSid, name = p.DisplayName,
                            dailyMinutes = p.DailyMinutes, enabled = p.Enabled,
                            bonusMinutes = p.BonusDay == today.ToString("yyyy-MM-dd") ? p.BonusMinutes : 0,
                            usedSeconds = (long)Math.Ceiling(usage.Seconds),
                            remainingSeconds = ScreenTimeRules.Remaining(p, usage, today)
                        };
                    }).ToList(),
                    updatedUtc = DateTimeOffset.UtcNow,
                    error = string.IsNullOrEmpty(_lastError) ? null : "Screen-time enforcement error. Check the service log."
                });
            }
            catch { return ScreenTimeReply.Failure("Could not read or save the protected rules. Check the service log."); }
        }
    }

    public void Dispose()
    {
        _control?.Dispose();
        _timer?.Dispose();
        lock (_sync)
        {
            _timer = null;
            ScreenTimeFiles.WriteAtomic(ScreenTimeFiles.UsagePath, _usage);
            try { File.Delete(ScreenTimeFiles.StatusPath); } catch { }
        }
    }
}
