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
    private readonly RemotePinStore _remotePin;
    private readonly string _protectedDirectory;
    private ScreenTimeControlServer? _control;
    private readonly HashSet<int> _asking = new();
    private readonly Dictionary<int, long> _locks = new();

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

    internal ScreenTimeService(Action<string> log, string protectedDirectory)
    {
        _log = log;
        // The remote PIN hash lives in the SYSTEM/Administrators-only folder, never in the
        // user-readable screen-time folder, so it cannot be attacked offline.
        _protectedDirectory = protectedDirectory;
        _remotePin = new RemotePinStore(Path.Combine(protectedDirectory, "remote-pin.json"));
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
            var utcNow = DateTimeOffset.UtcNow;
            var sessions = policies.Any(p => p.Enabled || ScreenTimeRules.IsLocked(p, utcNow)) ? ScreenTimeWindows.Sessions() : new();
            foreach (var policy in policies)
            {
                if (!_usage.TryGetValue(policy.ProfileId, out var usage))
                    _usage[policy.ProfileId] = usage = new();
                var userSessions = sessions.Where(s => s.Sid == policy.UserSid).ToList();
                ScreenTimeRules.Advance(usage, today,
                    policy.Enabled && !ScreenTimeRules.IsLocked(policy, utcNow) && userSessions.Any(s => s.Unlocked) ? elapsed : 0);
            }
            // A parent's "lock now": keep the account at the sign-in screen (programs keep running)
            // until the time is up. Signing back in just gets it locked again a few seconds later.
            foreach (var policy in policies.Where(p => ScreenTimeRules.IsLocked(p, utcNow)))
            {
                foreach (var session in sessions.Where(s => s.Sid == policy.UserSid && s.Unlocked))
                {
                    if (_locks.TryGetValue(session.SessionId, out var lastLock) && tick - lastLock < 5000) continue;
                    _locks[session.SessionId] = tick;
                    try { ScreenTimeWindows.Lock(session.SessionId, policy.UserSid); _log("Screen-time: parent lock; disconnected " + policy.UserSid); }
                    catch (Exception ex) { _log("Screen-time: could not lock session: " + ex.Message); }
                }
            }
            // Checkpoint periodically (and always right before a logoff) rather than every
            // second: a restart loses at most a few seconds of usage, without constant disk churn.
            PersistUsage(tick, force: false);
            foreach (var policy in policies.Where(p => p.Enabled))
            {
                var usage = _usage[policy.ProfileId];
                var remaining = ScreenTimeRules.Remaining(policy, usage, today);
                var now = DateTime.Now;
                var bedtime = ScreenTimeRules.InBedtime(policy, now);
                var warningKey = policy.ProfileId + ":" + usage.Day;
                if (!_warnings.TryGetValue(warningKey, out var sent)) _warnings[warningKey] = sent = new();
                var bedWarningKey = policy.ProfileId + ":bed:" + now.ToString("yyyy-MM-dd");
                if (!_warnings.TryGetValue(bedWarningKey, out var bedSent)) _warnings[bedWarningKey] = bedSent = new();
                var untilBed = ScreenTimeRules.MinutesUntilBedtime(policy, now);
                foreach (var session in sessions.Where(s => s.Sid == policy.UserSid))
                {
                    if (remaining == 0 || bedtime)
                    {
                        if (!_logoffs.TryGetValue(session.SessionId, out var last) || tick - last >= 5000)
                        {
                            PersistUsage(tick, force: true);
                            ScreenTimeWindows.Logoff(session.SessionId, policy.UserSid);
                            _logoffs[session.SessionId] = tick;
                            _log(bedtime ? "Screen-time: bedtime; logged off " + policy.UserSid
                                         : "Screen-time: quota exhausted; logged off " + policy.UserSid);
                        }
                    }
                    else if (session.Unlocked)
                    {
                        var threshold = new[] { 1, 5, 10 }.FirstOrDefault(m => remaining <= m * 60);
                        var bedThreshold = untilBed is { } left ? new[] { 1, 5, 10 }.FirstOrDefault(m => left <= m) : 0;
                        if (bedThreshold > 0 && !bedSent.Contains(bedThreshold) && (threshold == 0 || untilBed * 60 < remaining))
                        {
                            bedSent.Add(bedThreshold);
                            ScreenTimeWindows.Warn(session.SessionId,
                                $"Bedtime in about {untilBed} minute(s). Save your work - you will be signed out then.");
                        }
                        else if (threshold > 0 && !sent.Contains(threshold))
                        {
                            sent.Add(threshold);
                            var text = $"About {Math.Ceiling(remaining / 60.0)} minute(s) of PC time left today. Save your work - you will be signed out when time runs out.";
                            // At the 5 minute warning the child may ask for more time. The question is
                            // a Yes/No box, so it runs off the timer thread while it waits for an answer.
                            if (threshold == 5) AskForMoreTime(session.SessionId, policy, text);
                            else ScreenTimeWindows.Warn(session.SessionId, text);
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

    private void AskForMoreTime(int sessionId, ScreenTimePolicy policy, string text)
    {
        lock (_asking) { if (!_asking.Add(sessionId)) return; }
        var sid = policy.UserSid;
        var name = policy.DisplayName;
        Task.Run(() =>
        {
            try
            {
                if (!ScreenTimeWindows.Ask(sessionId, text + " Ask your parent for more time?")) return;
                var added = TimeRequestStore.Add(sid, name, DateTimeOffset.UtcNow);
                _log(added ? $"Screen-time: {name} asked for more time." : $"Screen-time: {name} asked again; a request is already waiting.");
                ScreenTimeWindows.Warn(sessionId, added ? "Your parent has been asked. You will be told if they add time."
                                                       : "Your parent has already been asked. Please wait for their answer.");
            }
            catch (Exception ex) { _log("Screen-time: could not record the request: " + ex.Message); }
            finally { lock (_asking) _asking.Remove(sessionId); }
        });
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
            remote = RemoteStatus(),
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

                // A remote change is authorized by the PIN (with lockout) instead of Windows
                // elevation; once it passes it is applied exactly like a locally approved save.
                var viaRemote = request.Command == "remote-save";
                if (viaRemote)
                {
                    // The PIN hash and lockout counters are only trustworthy if the monitored user
                    // cannot read or write their folder; if its permissions are wrong, fail closed.
                    if (!DirectoryProtection.IsProtectedFromUsers(_protectedDirectory))
                    {
                        _log("Remote PC Time change refused: the protected settings folder is not secure.");
                        return ScreenTimeReply.Failure("Remote changes are disabled because the protected settings folder is not secure. Check the GameHost service log.");
                    }
                    var check = _remotePin.Verify(request.Pin, DateTimeOffset.UtcNow);
                    if (check.Status != RemotePinStatus.Ok)
                    {
                        _log($"Remote PC Time change refused ({check.Status}).");
                        return ScreenTimeReply.Failure(check.Message);
                    }
                    request.Command = "save";
                }

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
                    policy.WeekendMinutes = request.WeekendMinutes;
                    policy.LockUntilUtc = ScreenTimeRules.ApplyLock(policy.LockUntilUtc, request.LockMinutes, DateTimeOffset.UtcNow);
                    policy.BedtimeStart = request.BedtimeStart ?? "";
                    policy.BedtimeEnd = request.BedtimeEnd ?? "";
                    policy.WeekendBedtimeStart = request.WeekendBedtimeStart ?? "";
                    var todayText = DateTime.Today.ToString("yyyy-MM-dd");
                    var previousBonus = existing != null && existing.BonusDay == todayText ? existing.BonusMinutes : 0;
                    policy.Enabled = request.Enabled;
                    policy.BonusDay = DateTime.Today.ToString("yyyy-MM-dd");
                    policy.BonusMinutes = request.BonusMinutes;
                    policy.Validate();
                    if (request.Command == "validate") return ScreenTimeReply.Success(new { valid = true });
                    if (existing == null) policies.Add(policy);
                    ScreenTimeFiles.WriteAtomic(ScreenTimeFiles.PoliciesPath, policies);
                    // Adding time (or turning control off) answers any waiting request from this account.
                    if (request.BonusMinutes > previousBonus || !policy.Enabled)
                    {
                        try { TimeRequestStore.Resolve(policy.UserSid); } catch (Exception ex) { _log("Screen-time: could not close request: " + ex.Message); }
                    }
                    File.AppendAllText(Path.Combine(ScreenTimeFiles.DirectoryPath, "admin-audit.log"),
                        $"{DateTimeOffset.Now:o} {(viaRemote ? "Remote PIN authorized" : "Windows admin authorized")} policy for {policy.UserSid}; minutes={policy.DailyMinutes}; weekend={policy.WeekendMinutes}; bedtime={policy.BedtimeStart}-{policy.BedtimeEnd}/{policy.WeekendBedtimeStart}; lockUntil={policy.LockUntilUtc:o}; enabled={policy.Enabled}; bonus={policy.BonusMinutes}{Environment.NewLine}");
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
                            dailyMinutes = p.DailyMinutes, weekendMinutes = p.WeekendMinutes, enabled = p.Enabled,
                            bedtimeStart = p.BedtimeStart, bedtimeEnd = p.BedtimeEnd, weekendBedtimeStart = p.WeekendBedtimeStart,
                            lockedUntilUtc = ScreenTimeRules.IsLocked(p, DateTimeOffset.UtcNow) ? p.LockUntilUtc : null,
                            bonusMinutes = p.BonusDay == today.ToString("yyyy-MM-dd") ? p.BonusMinutes : 0,
                            usedSeconds = (long)Math.Ceiling(usage.Seconds),
                            remainingSeconds = ScreenTimeRules.Remaining(p, usage, today)
                        };
                    }).ToList(),
                    remote = RemoteStatus(),
                    requests = TimeRequestStore.Pending(DateTimeOffset.UtcNow)
                        .Select(r => new { id = r.Id, userSid = r.UserSid, name = r.Name, createdUtc = r.CreatedUtc }).ToList(),
                    updatedUtc = DateTimeOffset.UtcNow,
                    error = string.IsNullOrEmpty(_lastError) ? null : "Screen-time enforcement error. Check the service log."
                });
            }
            catch { return ScreenTimeReply.Failure("Could not read or save the protected rules. Check the service log."); }
        }
    }

    private object RemoteStatus()
    {
        var status = _remotePin.GetStatus(DateTimeOffset.UtcNow);
        var secure = !status.Configured || DirectoryProtection.IsProtectedFromUsers(_protectedDirectory);
        return new { configured = status.Configured && secure, lockedUntilUtc = status.LockedUntil, lockouts = status.Lockouts };
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
