using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MonitorAndControl.ScreenTime;
using MonitorAndControl.Services;

namespace MonitorAndControl.Web;

public static class ScreenTimeEndpoints
{
    /// <summary>
    /// Called with a short description when a remote PC Time change succeeds or someone guesses a
    /// wrong remote PIN, so the parent can be alerted. Set by the host application.
    /// </summary>
    public static Action<string, bool>? RemoteEvent { get; set; }

    private static long _lastFailureReportTicks;

    // An alert that fails must never turn an already-applied change into an error for the parent.
    private static void Raise(string message, bool security)
    {
        try { RemoteEvent?.Invoke(message, security); }
        catch (Exception ex) { Logger.Instance.Error($"PC Time alert could not be sent: {ex.Message}"); }
    }

    // 403 only for "you are not allowed" outcomes; validation and service problems get their own codes.
    private static int StatusFor(string? error)
    {
        if (string.IsNullOrEmpty(error)) return 400;
        if (error.StartsWith("Wrong PIN", StringComparison.Ordinal) || error.StartsWith("Too many", StringComparison.Ordinal)
            || error.StartsWith("Remote changes are", StringComparison.Ordinal))
            return 403;
        if (error.StartsWith("Could not record", StringComparison.Ordinal)) return 503;
        return 400;
    }

    /// <param name="isAdminAuthenticated">
    /// Tells whether a non-local request carries a valid dashboard admin login. When null (tests,
    /// standalone use) only requests on the PC itself are served, exactly as before.
    /// </param>
    public static void Map(WebApplication app, Func<HttpContext, bool>? isAdminAuthenticated = null)
    {
        app.MapGet("/api/screen-time", async (HttpContext ctx) =>
        {
            // Reading accounts and usage is fine for a logged-in remote admin; only changes are restricted.
            if (!TrustedLocalRequest(ctx) && isAdminAuthenticated?.Invoke(ctx) != true)
                return Results.Json(new { error = "Open this dashboard on the child PC itself to manage accounts." }, statusCode: 403);
            try
            {
                var reply = await ScreenTimeClient.SendAsync(new ScreenTimeRequest(), ctx.RequestAborted);
                return reply.Ok ? Results.Json(reply.Data) : Results.Json(new { error = reply.Error }, statusCode: 400);
            }
            catch { return Results.Json(new { error = "The screen-time service is unavailable. Install or update the full package and check the GameHost service." }, statusCode: 503); }
        });
        app.MapPost("/api/screen-time/policy", async (HttpContext ctx) =>
        {
            if (!TrustedLocalRequest(ctx)) return Results.Json(new { error = "Screen-time changes require local access on this PC." }, statusCode: 403);
            ScreenTimeRequest? request = null;
            try
            {
                var bytes = new byte[16385];
                int size = 0, read;
                while (size < bytes.Length && (read = await ctx.Request.Body.ReadAsync(bytes.AsMemory(size), ctx.RequestAborted)) > 0) size += read;
                if (size > 16384) return Results.BadRequest(new { error = "Request too large." });
                try { request = JsonSerializer.Deserialize<ScreenTimeRequest>(bytes.AsSpan(0, size), ScreenTimeProtocol.Json); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
                if (request == null) return Results.BadRequest(new { error = "Select an account and set the limits." });
                request.Command = "prepare";
                var prepared = await ScreenTimeClient.SendAsync(request, ctx.RequestAborted);
                if (!prepared.Ok) return Results.BadRequest(new { error = prepared.Error });
                var ticket = prepared.Data!.Value.GetProperty("ticketId").GetString()!;
                try
                {
                    if (prepared.Data.Value.GetProperty("canApproveDirectly").GetBoolean())
                        await ScreenTimeClient.SendAsync(new ScreenTimeRequest { Command = "approve", TicketId = ticket }, ctx.RequestAborted);
                    else
                    {
                        var helper = Path.Combine(AppContext.BaseDirectory, "GameHost.exe");
                        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                            FileName = helper, Arguments = "--approve-screen-time " + ticket,
                            UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                        }) ?? throw new InvalidOperationException("Could not open the Windows authorization prompt.");
                        await process.WaitForExitAsync(ctx.RequestAborted);
                    }
                    var reply = await ScreenTimeClient.SendAsync(new ScreenTimeRequest { Command = "result", TicketId = ticket }, ctx.RequestAborted);
                    return reply.Ok ? Results.Json(reply.Data) : Results.Json(new { error = reply.Error }, statusCode: 400);
                }
                finally
                {
                    try { await ScreenTimeClient.SendAsync(new ScreenTimeRequest { Command = "cancel", TicketId = ticket }, CancellationToken.None); } catch { }
                }
            }
            catch (System.ComponentModel.Win32Exception) { return Results.BadRequest(new { error = "Windows authorization was canceled. No changes were applied." }); }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid configuration data." }); }
            catch { return Results.Json(new { error = "Could not reach the service. No confirmation that the change was saved was received." }, statusCode: 503); }
        });

        // Anyone may read whether the limits are sealed (the Limits page shows a notice); only the service changes it.
        app.MapGet("/api/limits/protection", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var file = LimitsSealStore.Read();
            var phase = LimitsSealStore.Phase(file, now);
            return Results.Json(new
            {
                phase = phase.ToString().ToLowerInvariant(),
                protectedLimits = phase is SealPhase.Enforce or SealPhase.Editing or SealPhase.SealNow && file?.Protected == true,
                editOpenUntilUtc = phase == SealPhase.Editing ? file?.OpenUntilUtc : null
            });
        });

        // Ends an approved editing window early so the new limits get sealed. It grants nothing, so no approval.
        app.MapPost("/api/limits/finish-editing", async (HttpContext ctx) =>
        {
            if ((!TrustedLocalRequest(ctx) && isAdminAuthenticated?.Invoke(ctx) != true) || !SameOriginOrNone(ctx))
                return Results.Json(new { error = "Sign in to the dashboard with the admin password first." }, statusCode: 403);
            try
            {
                var reply = await ScreenTimeClient.SendAsync(new ScreenTimeRequest { Command = "limits-close" }, ctx.RequestAborted);
                return reply.Ok ? Results.Json(reply.Data) : Results.Json(new { error = reply.Error }, statusCode: 400);
            }
            catch { return Results.Json(new { error = "The screen-time service is unavailable." }, statusCode: 503); }
        });

        // "Not now" on a child's request for more time: closes the request and tells the child. No approval
        // is needed because it grants nothing; it still needs the PC itself or an admin login.
        app.MapPost("/api/screen-time/dismiss-request", async (HttpContext ctx) =>
        {
            if ((!TrustedLocalRequest(ctx) && isAdminAuthenticated?.Invoke(ctx) != true) || !SameOriginOrNone(ctx))
                return Results.Json(new { error = "Sign in to the dashboard with the admin password first." }, statusCode: 403);
            try
            {
                var bytes = new byte[2049];
                int size = 0, read;
                while (size < bytes.Length && (read = await ctx.Request.Body.ReadAsync(bytes.AsMemory(size), ctx.RequestAborted)) > 0) size += read;
                if (size > 2048) return Results.BadRequest(new { error = "Request too large." });
                var request = JsonSerializer.Deserialize<ScreenTimeRequest>(bytes.AsSpan(0, size), ScreenTimeProtocol.Json);
                if (request == null || string.IsNullOrWhiteSpace(request.UserSid)) return Results.BadRequest(new { error = "Select an account." });
                var reply = await ScreenTimeClient.SendAsync(new ScreenTimeRequest { Command = "dismiss-request", UserSid = request.UserSid }, ctx.RequestAborted);
                return reply.Ok ? Results.Json(reply.Data) : Results.Json(new { error = reply.Error }, statusCode: 400);
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid request." }); }
            catch { return Results.Json(new { error = "The screen-time service is unavailable." }, statusCode: 503); }
        });

        // Remote change: authorized by the dashboard admin login AND the remote PIN, which the service
        // verifies (with lockout). There is no Windows UAC prompt on this path, by necessity.
        app.MapPost("/api/screen-time/remote-policy", async (HttpContext ctx) =>
        {
            if (isAdminAuthenticated?.Invoke(ctx) != true || !SameOriginOrNone(ctx))
                return Results.Json(new { error = "Sign in to the dashboard with the admin password first." }, statusCode: 403);
            try
            {
                var bytes = new byte[16385];
                int size = 0, read;
                while (size < bytes.Length && (read = await ctx.Request.Body.ReadAsync(bytes.AsMemory(size), ctx.RequestAborted)) > 0) size += read;
                if (size > 16384) return Results.BadRequest(new { error = "Request too large." });
                ScreenTimeRequest? request;
                try { request = JsonSerializer.Deserialize<ScreenTimeRequest>(bytes.AsSpan(0, size), ScreenTimeProtocol.Json); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
                if (request == null) return Results.BadRequest(new { error = "Select an account and set the limits." });
                if (string.IsNullOrWhiteSpace(request.Pin)) return Results.BadRequest(new { error = "Enter the remote PIN." });

                request.Command = "remote-save";
                var source = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var reply = await ScreenTimeClient.SendAsync(request, ctx.RequestAborted);
                if (reply.Ok)
                {
                    var sid = request.UserSid ?? "";
                    if (request.Operation == "limits-open")
                    {
                        Raise($"The app limits were opened for editing for {request.LimitsMinutes} min remotely from {source}.", true);
                        return Results.Json(reply.Data);
                    }
                    if (request.Operation == "limits-clear")
                    {
                        Raise($"The protection of the app limits was removed remotely from {source}.", true);
                        return Results.Json(reply.Data);
                    }
                    Raise($"PC Time was changed remotely from {source}: account ...{sid[^Math.Min(4, sid.Length)..]}, " +
                        $"{(request.Enabled ? request.DailyMinutes + " min/day" + (request.WeekendMinutes > 0 ? $", {request.WeekendMinutes} at weekends" : "") + (string.IsNullOrEmpty(request.BedtimeStart) ? "" : $", bedtime {request.BedtimeStart}-{request.BedtimeEnd}") : "control off")}{(request.LockMinutes > 0 ? $", locked for {request.LockMinutes} min" : request.LockMinutes < 0 ? ", unlocked" : "")}, bonus {request.BonusMinutes} min.", false);
                    return Results.Json(reply.Data);
                }

                // A wrong guess is worth an alert, but never more than one per 10 minutes (CAS keeps the throttle correct under concurrency).
                if (reply.Error is { } error && (error.StartsWith("Wrong PIN", StringComparison.Ordinal) || error.StartsWith("Too many", StringComparison.Ordinal)))
                {
                    var now = DateTime.UtcNow.Ticks;
                    var last = Interlocked.Read(ref _lastFailureReportTicks);
                    if (now - last > TimeSpan.FromMinutes(10).Ticks && Interlocked.CompareExchange(ref _lastFailureReportTicks, now, last) == last)
                        Raise($"A wrong remote PC Time PIN was entered from {source}.", true);
                }
                return Results.Json(new { error = reply.Error }, statusCode: StatusFor(reply.Error));
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid configuration data." }); }
            catch { return Results.Json(new { error = "Could not reach the service. No confirmation that the change was saved was received." }, statusCode: 503); }
        });
    }

    private static bool SameOriginOrNone(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin) || origin.Equals($"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrustedLocalRequest(HttpContext ctx)
    {
        var host = ctx.Request.Host.Host.Trim('[', ']');
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && host != "127.0.0.1" && host != "::1") return false;
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
        if (remote == null || !IPAddress.IsLoopback(remote)) return false;
        if (ctx.Request.Headers.Keys.Any(h => h.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
            || h.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase))) return false;
        var origin = ctx.Request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin) || origin.Equals($"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase);
    }
}
