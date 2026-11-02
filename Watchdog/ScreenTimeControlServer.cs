using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;
using MonitorAndControl.ScreenTime;

namespace MonitorAndControlWatchdog;

internal sealed class ScreenTimeControlServer : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<ScreenTimeRequest, ScreenTimeReply> _handle;
    private readonly Action<string> _log;
    private readonly Task _loop;
    private readonly ScreenTimeApprovals _approvals = new();
    private NamedPipeServerStream? _listener;
    private ScreenTimeReply? _snapshot;
    private DateTimeOffset _snapshotAt;

    private readonly string _monitorPath;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    internal ScreenTimeControlServer(Func<ScreenTimeRequest, ScreenTimeReply> handle, Action<string> log, string monitorPath)
    {
        _handle = handle; _log = log; _monitorPath = monitorPath;
        _listener = CreatePipe();
        _loop = Task.Run(ListenAsync);
    }
    private static NamedPipeServerStream CreatePipe()
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var type in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(type, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(ScreenTimeProtocol.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 32768, 32768, acl);
    }
    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = _listener ?? CreatePipe();
                _listener = null;
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var request = await ScreenTimeProtocol.ReadAsync<ScreenTimeRequest>(pipe, 524288, deadline.Token);
                string callerSid = "";
                bool elevated = false;
                pipe.RunAsClient(() => {
                    using var identity = WindowsIdentity.GetCurrent();
                    callerSid = identity.User?.Value ?? "";
                    elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                });
                var caller = CallerImage(pipe);
                var fromMonitor = caller != null && !string.IsNullOrEmpty(_monitorPath)
                    && string.Equals(Path.GetFullPath(caller), Path.GetFullPath(_monitorPath), StringComparison.OrdinalIgnoreCase);
                var reply = Dispatch(request, callerSid, elevated, fromMonitor, caller);
                await ScreenTimeProtocol.WriteAsync(pipe, reply, deadline.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch
            {
                if (!_stop.IsCancellationRequested)
                {
                    _log("Screen-time control request failed; payload not logged.");
                    await Task.Delay(200, _stop.Token);
                }
            }
        }
    }
    // The program on the other end of the pipe. A child cannot run a program from the protected install
    // folder, so this separates DeviceMon's own requests from a script a child wrote.
    private static string? CallerImage(NamedPipeServerStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) return null;
            using var process = Process.GetProcessById((int)pid);
            return process.MainModule?.FileName;
        }
        catch { return null; }
    }

    private ScreenTimeReply Dispatch(ScreenTimeRequest request, string callerSid, bool elevated, bool fromMonitor, string? caller)
    {
        if (string.IsNullOrEmpty(callerSid)) return ScreenTimeReply.Failure("Local identity not recognized.");
        var now = DateTimeOffset.UtcNow;
        switch (request.Command)
        {
            case "snapshot":
                if (_snapshot == null || now - _snapshotAt > TimeSpan.FromSeconds(1))
                {
                    _snapshot = _handle(request); _snapshotAt = now;
                }
                return _snapshot;
            case "prepare":
                request.Command = "validate";
                var validation = _handle(request);
                if (!validation.Ok) return validation;
                try
                {
                    var ticket = _approvals.Create(callerSid, new ScreenTimeChange(request.UserSid,
                        request.DailyMinutes, request.BonusMinutes, request.Enabled, request.WeekendMinutes,
                        request.BedtimeStart, request.BedtimeEnd, request.WeekendBedtimeStart, request.LockMinutes,
                        request.Operation, request.LimitsMinutes), now);
                    return ScreenTimeReply.Success(new { ticketId = ticket, canApproveDirectly = elevated });
                }
                catch (InvalidOperationException ex) { return ScreenTimeReply.Failure(ex.Message); }
            case "approve":
                if (!_approvals.TryClaim(request.TicketId, elevated, now, out var change))
                    return ScreenTimeReply.Failure("This change needs Windows administrator authorization and a valid request.");
                var saved = _handle(new ScreenTimeRequest {
                    Command = "save", UserSid = change!.UserSid, DailyMinutes = change.DailyMinutes,
                    BonusMinutes = change.BonusMinutes, Enabled = change.Enabled, WeekendMinutes = change.WeekendMinutes,
                    BedtimeStart = change.BedtimeStart, BedtimeEnd = change.BedtimeEnd, WeekendBedtimeStart = change.WeekendBedtimeStart,
                    LockMinutes = change.LockMinutes, Operation = change.Operation, LimitsMinutes = change.LimitsMinutes
                });
                _approvals.Complete(request.TicketId, saved);
                if (saved.Ok)
                {
                    _snapshot = null;
                    _log($"Screen-time policy saved by elevated Windows identity {callerSid} for {change.UserSid}.");
                }
                return saved;
            case "remote-save":
                // Authorized by the remote PIN inside the handler (not by Windows elevation), with
                // brute-force lockout. This is the only change path that does not need UAC.
                var remote = _handle(request);
                if (remote.Ok)
                {
                    _snapshot = null;
                    _log($"Screen-time policy changed through the remote PIN (requested by {callerSid}).");
                }
                return remote;
            case "homework-start":
            case "set-blocked-sites":
            case "limits-seal":
            case "limits-close":
            case "dismiss-request":
                // These need no approval, but they must come from DeviceMon itself (which holds the dashboard),
                // not from another program the child runs: otherwise an empty block list or a forged seal
                // would be one pipe message away.
                if (!fromMonitor)
                {
                    _log($"Refused {request.Command} from {(caller ?? "an unknown program")}: only DeviceMon may send it.");
                    return ScreenTimeReply.Failure("Only DeviceMon can do this.");
                }
                _snapshot = null;
                return _handle(request);
            case "result": return _approvals.Result(request.TicketId, callerSid, now);
            case "cancel":
                _approvals.Cancel(request.TicketId, callerSid);
                return ScreenTimeReply.Success(new { canceled = true });
            default: return ScreenTimeReply.Failure("Unknown operation.");
        }
    }
    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Dispose();
        try { _loop.Wait(TimeSpan.FromSeconds(6)); } catch { }
        _stop.Dispose();
    }
}
