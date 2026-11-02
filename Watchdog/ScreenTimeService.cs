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
    private readonly BrowserPolicy _sites;
    private long _lastPolicy;
    private long _lastHomeworkCheck;
    private bool _lastHomework;
    private long _lastZoneCheck;
    private long _lastActiveCheck;
    private IReadOnlyCollection<string> _activeSids = Array.Empty<string>();
    // Accounts whose DeviceMon reports an always-allowed app in front: that time is not PC time (until the report goes stale).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _exemptUntil = new();
    private readonly string _heartbeatPath;
    private readonly System.Threading.Timer _heartbeatTimer;
    // The check-in connects only to public addresses, also when a name resolves to a home-network one.
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, token) =>
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
            var target = addresses.FirstOrDefault(Heartbeat.IsPublicAddress)
                ?? throw new HttpRequestException("The check-in address is not on the internet.");
            if (addresses.Any(a => !Heartbeat.IsPublicAddress(a)))
                throw new HttpRequestException("The check-in address is not on the internet.");
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new System.Net.IPEndPoint(target, context.DnsEndPoint.Port), token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }) { Timeout = TimeSpan.FromSeconds(15) };
    private DateTimeOffset? _heartbeatOkUtc;
    private string _heartbeatError = "";
    private int _heartbeatRunning;

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

    internal ScreenTimeService(Action<string> log, string protectedDirectory, string monitorPath = "")
    {
        _log = log;
        // The remote PIN hash lives in the SYSTEM/Administrators-only folder, never in the
        // user-readable screen-time folder, so it cannot be attacked offline.
        _protectedDirectory = protectedDirectory;
        _remotePin = new RemotePinStore(Path.Combine(protectedDirectory, "remote-pin.json"));
        PrepareDirectory();
        _sites = new BrowserPolicy(Path.Combine(protectedDirectory, "site-blocklist.json"), log);
        _heartbeatPath = Path.Combine(protectedDirectory, "heartbeat.json");
        CheckTimeZone();
        _activeSids = ActiveSids();
        ApplySiteBlocking();
        _lastHomework = HomeworkState.IsActiveNow();
        _usage = ReadUsage();
        _lastTick = _clock.ElapsedMilliseconds;
        try { _control = new ScreenTimeControlServer(HandleControl, _log, monitorPath); }
        catch { _log("Screen-time configuration channel unavailable; enforcement continues."); }
        _timer = new System.Threading.Timer(Tick, null, 1000, 1000);
        _heartbeatTimer = new System.Threading.Timer(_ => _ = PingAsync(), null, TimeSpan.FromMinutes(1), Heartbeat.Interval);
    }

    // The account at the screen right now (empty for an administrator or the sign-in screen).
    // Every account with an active session (normally one; more with a remote desktop session).
    private static IReadOnlyCollection<string> ActiveSids(IEnumerable<ScreenTimeWindows.UserSession>? sessions = null)
    {
        try { return (sessions ?? ScreenTimeWindows.Sessions()).Select(s => s.Sid).Distinct().Order(StringComparer.Ordinal).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    // Windows lets anyone change the time zone. The rules keep the pinned zone; a change is only recorded
    // (DeviceMon tells the parent, who can accept the new zone with approval).
    private void CheckTimeZone()
    {
        try
        {
            TimeZoneInfo.ClearCachedData();
            var current = ParentClock.Read();
            var next = ParentClock.Observe(current, TimeZoneInfo.Local.Id, DateTimeOffset.UtcNow);
            if (current != null && current.PinnedId == next.PinnedId && current.SystemId == next.SystemId && current.ChangedUtc == next.ChangedUtc) return;
            ScreenTimeFiles.WriteAtomic(ParentClock.Path, next);
            ParentClock.Invalidate();
            if (current == null) _log($"Rules use the time zone {next.PinnedId}.");
            else if (current.SystemId != next.SystemId)
                _log(next.ChangedUtc == null ? $"The time zone is back to {next.PinnedId}."
                    : $"The time zone was changed to {next.SystemId}; the rules keep using {next.PinnedId} until a parent accepts the change.");
        }
        catch (Exception ex) { _log("Time zone check failed: " + ex.Message); }
    }

    private string ReadHeartbeatUrl()
    {
        try
        {
            if (!File.Exists(_heartbeatPath)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(_heartbeatPath));
            var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            return Heartbeat.IsValidUrl(url) ? url : "";
        }
        catch { return ""; }
    }

    // A check-in to an outside service (healthchecks.io or similar). When the check-ins stop - the PC is off,
    // offline or this service was stopped - that service alerts the parent.
    private async Task PingAsync()
    {
        if (Interlocked.Exchange(ref _heartbeatRunning, 1) != 0) return;
        try
        {
            var url = ReadHeartbeatUrl();
            if (url.Length == 0) { _heartbeatError = ""; return; }
            using var response = await _http.GetAsync(url);
            if (response.IsSuccessStatusCode) { _heartbeatOkUtc = DateTimeOffset.UtcNow; _heartbeatError = ""; }
            else _heartbeatError = "The check-in address answered " + (int)response.StatusCode + ".";
        }
        catch (Exception ex) { _heartbeatError = "The check-in failed: " + ex.GetType().Name + "."; }
        finally { Volatile.Write(ref _heartbeatRunning, 0); }
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
                if (result.Values.Any(u => !double.IsFinite(u.Seconds) || u.Seconds < 0 || !double.IsFinite(u.RunSeconds)
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
            var today = DateOnly.FromDateTime(ParentClock.Now);
            var policies = ScreenTimeFiles.ReadPolicies();
            var utcNow = DateTimeOffset.UtcNow;
            var sessions = policies.Any(p => p.Enabled || ScreenTimeRules.IsLocked(p, utcNow)) ? ScreenTimeWindows.Sessions() : new();
            foreach (var policy in policies)
            {
                if (!_usage.TryGetValue(policy.ProfileId, out var usage))
                    _usage[policy.ProfileId] = usage = new();
                var userSessions = sessions.Where(s => s.Sid == policy.UserSid).ToList();
                var inUse = policy.Enabled && !ScreenTimeRules.IsLocked(policy, utcNow) && !ScreenTimeRules.OnBreak(usage, utcNow)
                            && userSessions.Any(s => s.Unlocked);
                var exempt = _exemptUntil.TryGetValue(policy.UserSid, out var exemptUntil) && exemptUntil > utcNow;
                ScreenTimeRules.Advance(usage, today, inUse && !exempt ? elapsed : 0);
                // Break reminders count all use in a row, also in always-allowed apps (it is about resting the eyes).
                var (startBreak, warnMinutes) = ScreenTimeRules.AdvanceBreak(policy, usage, utcNow, inUse ? elapsed : 0);
                if (startBreak)
                {
                    PersistUsage(tick, force: true);
                    _log($"Screen-time: break for {policy.UserSid} until {usage.BreakUntilUtc:O}.");
                    foreach (var session in userSessions.Where(s => s.Unlocked))
                        ScreenTimeWindows.Warn(session.SessionId, $"Time for a {policy.BreakMinutes}-minute break. The PC locks now and can be used again at {ParentClock.ToRuleTime(usage.BreakUntilUtc!.Value):HH:mm}.");
                }
                else if (warnMinutes > 0)
                    foreach (var session in userSessions.Where(s => s.Unlocked))
                        ScreenTimeWindows.Warn(session.SessionId, $"A {policy.BreakMinutes}-minute break starts in about {warnMinutes} minute(s). Save your work.");
            }
            // A parent's "lock now" or a break: keep the account at the sign-in screen (programs keep running)
            // until the time is up. Signing back in just gets it locked again a few seconds later.
            foreach (var policy in policies.Where(p => ScreenTimeRules.IsLocked(p, utcNow)
                         || p.Enabled && p.BreakAfterMinutes > 0 && _usage.TryGetValue(p.ProfileId, out var u) && ScreenTimeRules.OnBreak(u, utcNow)))
            {
                foreach (var session in sessions.Where(s => s.Sid == policy.UserSid && s.Unlocked))
                {
                    if (_locks.TryGetValue(session.SessionId, out var lastLock) && tick - lastLock < 5000) continue;
                    _locks[session.SessionId] = tick;
                    try { ScreenTimeWindows.Lock(session.SessionId, policy.UserSid); _log((ScreenTimeRules.IsLocked(policy, utcNow) ? "Screen-time: parent lock; disconnected " : "Screen-time: break; disconnected ") + policy.UserSid); }
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
                var now = ParentClock.Now;
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
            // Browsers can lose their policy (a Windows policy refresh, a reset); put the block list back now and then.
            if (tick - _lastPolicy >= 300000) { _lastPolicy = tick; ApplySiteBlocking(); }
            if (tick - _lastZoneCheck >= 30000) { _lastZoneCheck = tick; CheckTimeZone(); }
            // Rules for one account are in the browsers only while that account is at the screen.
            if (tick - _lastActiveCheck >= 5000)
            {
                _lastActiveCheck = tick;
                var active = ActiveSids(sessions.Count > 0 ? sessions : null);
                if (!active.SequenceEqual(_activeSids)) { _activeSids = active; ApplySiteBlocking(); }
            }
            // Homework mode ends by itself: switch the browser rules back as soon as the time is up.
            if (tick - _lastHomeworkCheck >= 5000)
            {
                _lastHomeworkCheck = tick;
                var homework = HomeworkState.IsActiveNow();
                if (homework != _lastHomework) { _lastHomework = homework; ApplySiteBlocking(); _log(homework ? "Homework mode started." : "Homework mode ended."); }
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
            try { WriteStatus(new(), DateOnly.FromDateTime(ParentClock.Now), ex.Message); } catch { }
        }
        finally { Monitor.Exit(_sync); }
    }

    // Sites the parent blocked outright and sealed are enforced from the sealed copy, whatever DeviceMon reports.
    private void ApplySiteBlocking()
    {
        try
        {
            var file = LimitsSealStore.Read();
            var sealedContent = LimitsSealStore.Phase(file, DateTimeOffset.UtcNow) != SealPhase.Off ? file?.Content : null;
            _sites.Apply(sealedContent, HomeworkState.IsActiveNow(), _activeSids);
        }
        catch (Exception ex) { _log("Site blocking could not be applied: " + ex.Message); }
    }

    private static object DescribeLimitsProtection()
    {
        var now = DateTimeOffset.UtcNow;
        var file = LimitsSealStore.Read();
        var phase = LimitsSealStore.Phase(file, now);
        return new
        {
            phase = phase.ToString().ToLowerInvariant(),
            sealedUtc = file?.SealedUtc,
            editOpenUntilUtc = phase == SealPhase.Editing ? file?.OpenUntilUtc : null
        };
    }

    // "New programs need my OK". Only DeviceMon sends these (checked by the control server), and the
    // account is the caller's. Allowing a program is an approved operation, never one of these.
    private ScreenTimeReply HandlePrograms(ScreenTimeRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var programs = ProgramApprovals.Read();
        switch (request.Command)
        {
            case "programs-on":
                ProgramApprovals.Enable(programs, now);
                ProgramApprovals.Write(programs);
                _log("New programs now need the parent's OK.");
                return ScreenTimeReply.Success(new { enabled = true });
            case "program-baseline":
                if (!IsChildAccount(request.UserSid)) return ScreenTimeReply.Success(new { state = "exempt" });
                if (ProgramApprovals.Baseline(programs, request.UserSid, request.ProgramNames ?? new(), now))
                {
                    ProgramApprovals.Write(programs);
                    _log($"Programs already on the PC for {request.UserSid} were recorded as allowed.");
                }
                return ScreenTimeReply.Success(new { state = "done" });
            case "program-block":
            {
                if (ProgramApprovals.Key(request.ProgramName) is not { } key) return ScreenTimeReply.Failure("Invalid program.");
                var entry = ProgramApprovals.Find(programs, key);
                // Blocking only restricts, so it needs no approval (unlike allowing).
                if (entry == null) return ScreenTimeReply.Failure("This program is not in the list. Reload the page.");
                var wasWaiting = entry.State == ProgramApprovals.Waiting;
                ProgramApprovals.Decide(programs, key, allow: false);
                ProgramApprovals.Write(programs);
                _log($"The parent kept the program {key} blocked.");
                if (wasWaiting) TellAccount(entry.UserSid, $"Your parent did not allow {ProgramLabel(entry)}.");
                return ScreenTimeReply.Success(new { blocked = true, wasWaiting });
            }
            default: // program-request
            {
                if (ProgramApprovals.Key(request.ProgramName) is not { } key) return ScreenTimeReply.Failure("Invalid program.");
                // The parent's own (administrator) accounts are never held up.
                if (!IsChildAccount(request.UserSid)) return ScreenTimeReply.Success(new { state = "exempt" });
                if (!programs.Enabled) return ScreenTimeReply.Success(new { state = ProgramApprovals.Allowed });
                if (!programs.Baselined.Contains(request.UserSid)) return ScreenTimeReply.Success(new { state = "baseline" });
                if (ProgramApprovals.Request(programs, key, request.ProgramDisplay, request.UserSid, request.AccountName, now) is not { } result)
                    return ScreenTimeReply.Success(new { state = ProgramApprovals.Blocked, isNew = false });
                if (result.IsNew)
                {
                    ProgramApprovals.Write(programs);
                    _log($"New program {key} opened by {request.UserSid}; waiting for the parent.");
                }
                return ScreenTimeReply.Success(new { state = result.Entry.State, id = result.Entry.Id, isNew = result.IsNew });
            }
        }
    }

    // Decided by the account itself (standard, enabled), not by its session: a parent's session runs with a
    // UAC-filtered token that hides the Administrators group, and a child's session may be briefly disconnected.
    private sealed record ChildAccounts(DateTime At, HashSet<string> Sids);
    private ChildAccounts? _childAccounts;

    private bool IsChildAccount(string sid)
    {
        if (sid.Length == 0) return false;
        try
        {
            var known = _childAccounts;
            if (known == null || DateTime.UtcNow - known.At > TimeSpan.FromMinutes(1))
                _childAccounts = known = new(DateTime.UtcNow, ScreenTimeWindows.Accounts().Where(a => a.Eligible).Select(a => a.Sid).ToHashSet());
            return known.Sids.Contains(sid);
        }
        catch { return false; }
    }

    private static string ProgramLabel(ProgramEntry entry) => entry.Display.Length > 0 ? entry.Display : entry.Name;

    private void TellAccount(string sid, string text)
    {
        try
        {
            foreach (var session in ScreenTimeWindows.Sessions().Where(s => s.Sid == sid && s.Unlocked))
                ScreenTimeWindows.Warn(session.SessionId, text);
        }
        catch (Exception ex) { _log("Could not show a message: " + ex.Message); }
    }

    private static object DescribePrograms()
    {
        var programs = ProgramApprovals.Read();
        return new
        {
            enabled = programs.Enabled,
            list = ProgramApprovals.Visible(programs).Take(100).Select(e => new
            {
                id = e.Id, name = e.Name, display = e.Display, state = e.State, userSid = e.UserSid, account = e.Account, seenUtc = e.SeenUtc
            }).ToList()
        };
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
            heartbeat = new { configured = ReadHeartbeatUrl().Length > 0, lastOkUtc = _heartbeatOkUtc, error = _heartbeatError.Length > 0 ? _heartbeatError : null },
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
        if (request.Command == "pc-time-exempt")
        {
            // DeviceMon (checked by the control server) reports that an always-allowed app is in front of the
            // calling account; the report lapses on its own if DeviceMon stops sending it.
            if (request.UserSid.Length > 0) _exemptUntil[request.UserSid] = DateTimeOffset.UtcNow.AddSeconds(8);
            return ScreenTimeReply.Success(new { exempt = true });
        }
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

                if (request.Command == "homework-start")
                {
                    // Starting only restricts, so it needs no approval; ending it early does (see homework-stop).
                    var state = HomeworkState.Start(request.HomeworkMinutes, DateTimeOffset.UtcNow);
                    // Starting only restricts: a later start can extend a running session but never cut it short.
                    if (HomeworkState.Read() is { UntilUtc: { } running } existing && running > state.UntilUtc) state = existing;
                    HomeworkState.Write(state);
                    _lastHomework = true;
                    ApplySiteBlocking();
                    _log($"Homework mode started until {state.UntilUtc:O}.");
                    try
                    {
                        foreach (var session in ScreenTimeWindows.Sessions().Where(s => s.Unlocked))
                            ScreenTimeWindows.Warn(session.SessionId, $"Homework mode is on until {ParentClock.ToRuleTime(state.UntilUtc!.Value):HH:mm}. Games and entertainment are blocked until then.");
                    }
                    catch { /* the mode is on even if the message cannot be shown */ }
                    return ScreenTimeReply.Success(new { untilUtc = state.UntilUtc });
                }

                if (request.Command == "set-blocked-sites")
                {
                    var hosts = SiteBlocking.NormalizeAll(request.BlockedHosts);
                    var allowed = SiteBlocking.NormalizeAll(request.AllowedHosts);
                    // UserSid is the calling account, filled in by the control server (never taken from the message).
                    var ownBlocked = SiteBlocking.NormalizeAll(request.AccountBlockedHosts);
                    var ownAllowed = SiteBlocking.NormalizeAll(request.AccountAllowedHosts);
                    if (_sites.SetReported(hosts, allowed, request.AllowOnly, request.SafeBrowsing, request.UserSid, ownBlocked, ownAllowed))
                    {
                        _log($"Site blocking: {hosts.Count} website(s) blocked, {(request.AllowOnly ? allowed.Count + " allowed (only listed sites may open)" : "all others allowed")}, safe browsing {(request.SafeBrowsing ? "on" : "off")}" +
                             (ownBlocked.Count + ownAllowed.Count > 0 ? $"; {ownBlocked.Count} blocked and {ownAllowed.Count} allowed for one account." : "."));
                        ApplySiteBlocking();
                    }
                    return ScreenTimeReply.Success(new { blocked = hosts.Count });
                }

                if (request.Command is "programs-on" or "program-baseline" or "program-request" or "program-block")
                    return HandlePrograms(request);

                if (request.Command == "set-heartbeat")
                {
                    var url = (request.HeartbeatUrl ?? "").Trim();
                    if (!Heartbeat.IsValidUrl(url)) return ScreenTimeReply.Failure("The check-in address must be an https address on the internet.");
                    if (url != ReadHeartbeatUrl())
                    {
                        ScreenTimeFiles.WriteAtomic(_heartbeatPath, new { url });
                        _heartbeatOkUtc = null;
                        _log(url.Length == 0 ? "Check-ins turned off." : "Check-in address set; the service checks in every 5 minutes.");
                        if (url.Length > 0) _ = PingAsync();
                    }
                    return ScreenTimeReply.Success(new { configured = url.Length > 0 });
                }

                if (request.Command == "limits-close")
                {
                    var open = LimitsSealStore.Read();
                    if (open != null) LimitsSealStore.Write(LimitsSealStore.Close(open, DateTimeOffset.UtcNow));
                    return ScreenTimeReply.Success(new { closed = true });
                }
                if (request.Command == "limits-seal")
                {
                    SealedLimits? content;
                    try { content = System.Text.Json.JsonSerializer.Deserialize<SealedLimits>(request.LimitsJson ?? "")?.Normalized(); }
                    catch { content = null; }
                    if (content == null || !LimitsSealStore.TrySeal(LimitsSealStore.Read(), content, DateTimeOffset.UtcNow, out var sealedFile))
                        return ScreenTimeReply.Failure("No limits editing window is waiting to be sealed.");
                    LimitsSealStore.Write(sealedFile);
                    ApplySiteBlocking();
                    _log($"Screen-time: app limits sealed ({content.Limits.Count} limits, {content.Schedule.Count} schedule rules, {content.Groups.Count} groups).");
                    return ScreenTimeReply.Success(new { sealedAt = sealedFile.SealedUtc });
                }

                if (request.Command == "dismiss-request")
                {
                    if (string.IsNullOrEmpty(request.UserSid)) return ScreenTimeReply.Failure("Select an account.");
                    var closed = TimeRequestStore.Resolve(request.UserSid, requestId: request.RequestId);
                    if (closed > 0)
                    {
                        _log("Screen-time: a parent declined a request for more time.");
                        try
                        {
                            foreach (var session in ScreenTimeWindows.Sessions().Where(s => s.Sid == request.UserSid && s.Unlocked))
                                ScreenTimeWindows.Warn(session.SessionId, "Your parent did not add more time this time.");
                        }
                        catch { /* the answer is recorded even if the message cannot be shown */ }
                    }
                    return ScreenTimeReply.Success(new { dismissed = closed });
                }

                if (request.Command is "save" or "validate" && !string.IsNullOrEmpty(request.Operation))
                {
                    // Sealed app limits: approved like any other change, but not tied to an account.
                    if (request.Operation == "limits-open" && request.LimitsMinutes is < 1 or > LimitsSealStore.MaxWindowMinutes)
                        return ScreenTimeReply.Failure("Choose between 1 and 60 minutes.");
                    if (request.Operation is not ("limits-open" or "limits-clear" or "homework-stop" or "timezone-accept" or "programs-off" or "program-allow"))
                        return ScreenTimeReply.Failure("Unknown operation.");
                    if (request.Operation == "program-allow"
                        && (ProgramApprovals.Key(request.ProgramName) is not { } allowKey || ProgramApprovals.Find(ProgramApprovals.Read(), allowKey) == null))
                        return ScreenTimeReply.Failure("This program is not in the list. Reload the page.");
                    // The parent approves the zone they were shown; if Windows was switched again meanwhile,
                    // that newer zone is not pinned by the old approval.
                    if (request.Operation == "timezone-accept")
                    {
                        TimeZoneInfo.ClearCachedData();
                        if (string.IsNullOrEmpty(request.TimeZoneId) || request.TimeZoneId != TimeZoneInfo.Local.Id)
                            return ScreenTimeReply.Failure("The PC's time zone changed again. Reload the page and check it.");
                    }
                    if (request.Command == "validate") return ScreenTimeReply.Success(new { valid = true });
                    var now = DateTimeOffset.UtcNow;
                    if (request.Operation == "limits-open")
                        LimitsSealStore.Write(LimitsSealStore.Open(LimitsSealStore.Read(), request.LimitsMinutes, now));
                    else if (request.Operation == "timezone-accept")
                    {
                        ScreenTimeFiles.WriteAtomic(ParentClock.Path, ParentClock.Accept(request.TimeZoneId));
                        ParentClock.Invalidate();
                        _log($"The rules now use the time zone {request.TimeZoneId}.");
                    }
                    else if (request.Operation == "programs-off")
                    {
                        var programs = ProgramApprovals.Read();
                        programs.Enabled = false;
                        ProgramApprovals.Write(programs);
                        _log("New programs no longer need the parent's OK.");
                    }
                    else if (request.Operation == "program-allow")
                    {
                        var programs = ProgramApprovals.Read();
                        var entry = ProgramApprovals.Decide(programs, ProgramApprovals.Key(request.ProgramName)!, allow: true)!;
                        ProgramApprovals.Write(programs);
                        _log($"The parent allowed the program {entry.Name}.");
                        TellAccount(entry.UserSid, $"Your parent allowed {ProgramLabel(entry)}. You can open it now.");
                    }
                    else if (request.Operation == "homework-stop")
                    {
                        HomeworkState.Clear();
                        _lastHomework = false;
                        ApplySiteBlocking();
                    }
                    else
                        LimitsSealStore.Clear();
                    File.AppendAllText(Path.Combine(ScreenTimeFiles.DirectoryPath, "admin-audit.log"),
                        $"{DateTimeOffset.Now:o} {(viaRemote ? "Remote PIN authorized" : "Windows admin authorized")} {request.Operation} minutes={request.LimitsMinutes}{Environment.NewLine}");
                    return ScreenTimeReply.Success(new { saved = true, operation = request.Operation });
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
                    var previousBreak = (policy.BreakAfterMinutes, policy.BreakMinutes);
                    policy.DisplayName = account?.Name ?? policy.DisplayName;
                    policy.DailyMinutes = request.DailyMinutes;
                    policy.WeekendMinutes = request.WeekendMinutes;
                    policy.LockUntilUtc = ScreenTimeRules.ApplyLock(policy.LockUntilUtc, request.LockMinutes, DateTimeOffset.UtcNow);
                    policy.BedtimeStart = request.BedtimeStart ?? "";
                    policy.BedtimeEnd = request.BedtimeEnd ?? "";
                    policy.WeekendBedtimeStart = request.WeekendBedtimeStart ?? "";
                    policy.BreakAfterMinutes = request.BreakAfterMinutes;
                    policy.BreakMinutes = request.BreakAfterMinutes > 0 ? request.BreakMinutes : 0;
                    var todayText = ParentClock.Today.ToString("yyyy-MM-dd");
                    var previousBonus = existing != null && existing.BonusDay == todayText ? existing.BonusMinutes : 0;
                    policy.Enabled = request.Enabled;
                    policy.BonusDay = todayText;
                    policy.BonusMinutes = request.BonusMinutes;
                    policy.Validate();
                    if (request.Command == "validate") return ScreenTimeReply.Success(new { valid = true });
                    if (existing == null) policies.Add(policy);
                    ScreenTimeFiles.WriteAtomic(ScreenTimeFiles.PoliciesPath, policies);
                    // Unlock now, or breaks turned off, ends a break in progress; a shorter break length shortens it.
                    if (_usage.TryGetValue(policy.ProfileId, out var run))
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (request.LockMinutes < 0 || policy.BreakAfterMinutes == 0) { run.BreakUntilUtc = null; run.RunSeconds = 0; run.RunWarned = 0; }
                        else if (previousBreak != (policy.BreakAfterMinutes, policy.BreakMinutes))
                        {
                            if (run.BreakUntilUtc is { } until && until > now.AddMinutes(policy.BreakMinutes)) run.BreakUntilUtc = now.AddMinutes(policy.BreakMinutes);
                            run.RunWarned = 0;
                        }
                        PersistUsage(_clock.ElapsedMilliseconds, force: true);
                    }
                    // Adding time (or turning control off) answers any waiting request from this account.
                    if (request.BonusMinutes > previousBonus || !policy.Enabled)
                    {
                        try { TimeRequestStore.Resolve(policy.UserSid); } catch (Exception ex) { _log("Screen-time: could not close request: " + ex.Message); }
                    }
                    File.AppendAllText(Path.Combine(ScreenTimeFiles.DirectoryPath, "admin-audit.log"),
                        $"{DateTimeOffset.Now:o} {(viaRemote ? "Remote PIN authorized" : "Windows admin authorized")} policy for {policy.UserSid}; minutes={policy.DailyMinutes}; weekend={policy.WeekendMinutes}; bedtime={policy.BedtimeStart}-{policy.BedtimeEnd}/{policy.WeekendBedtimeStart}; break={policy.BreakAfterMinutes}/{policy.BreakMinutes}; lockUntil={policy.LockUntilUtc:o}; enabled={policy.Enabled}; bonus={policy.BonusMinutes}{Environment.NewLine}");
                    return ScreenTimeReply.Success(new { saved = true, userSid = policy.UserSid });
                }
                foreach (var policy in policies)
                    if (!accounts.Any(a => a.Sid == policy.UserSid))
                        accounts.Add(new ScreenTimeWindows.Account(policy.DisplayName, policy.UserSid, false, "Account not found"));
                var today = DateOnly.FromDateTime(ParentClock.Now);
                var zone = ParentClock.Read();
                return ScreenTimeReply.Success(new {
                    accounts,
                    profiles = policies.Select(p => {
                        var usage = _usage.GetValueOrDefault(p.ProfileId) ?? new ScreenTimeUsage();
                        return new {
                            profileId = p.ProfileId, userSid = p.UserSid, name = p.DisplayName,
                            dailyMinutes = p.DailyMinutes, weekendMinutes = p.WeekendMinutes, enabled = p.Enabled,
                            bedtimeStart = p.BedtimeStart, bedtimeEnd = p.BedtimeEnd, weekendBedtimeStart = p.WeekendBedtimeStart,
                            lockedUntilUtc = ScreenTimeRules.IsLocked(p, DateTimeOffset.UtcNow) ? p.LockUntilUtc : null,
                            breakAfterMinutes = p.BreakAfterMinutes, breakMinutes = p.BreakMinutes,
                            breakUntilUtc = ScreenTimeRules.OnBreak(usage, DateTimeOffset.UtcNow) ? usage.BreakUntilUtc : null,
                            bonusMinutes = p.BonusDay == today.ToString("yyyy-MM-dd") ? p.BonusMinutes : 0,
                            usedSeconds = (long)Math.Ceiling(usage.Seconds),
                            remainingSeconds = ScreenTimeRules.Remaining(p, usage, today)
                        };
                    }).ToList(),
                    remote = RemoteStatus(),
                    limitsProtection = DescribeLimitsProtection(),
                    timeZone = new { pinned = zone?.PinnedId ?? TimeZoneInfo.Local.Id, system = zone?.SystemId ?? TimeZoneInfo.Local.Id, changedUtc = zone?.ChangedUtc },
                    programs = DescribePrograms(),
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
        _heartbeatTimer.Dispose();
        _http.Dispose();
        lock (_sync)
        {
            _timer = null;
            ScreenTimeFiles.WriteAtomic(ScreenTimeFiles.UsagePath, _usage);
            try { File.Delete(ScreenTimeFiles.StatusPath); } catch { }
        }
    }
}
