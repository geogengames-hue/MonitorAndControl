using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using MonitorAndControl.Data;
using MonitorAndControl.Models;
using MonitorAndControl.Services;

namespace MonitorAndControl.Web;

public class DashboardServer
{
    private static WebApplication? _app;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static int Port { get; set; } = 5000;
    private static string _adminToken = "";
    private static bool _adminPasswordSet;

    public static async Task StartAsync(UsageDatabase db, WindowTracker tracker, LimitEnforcer enforcer,
        SchedulerService scheduler, EmailService emailService, AppConfig config)
    {
        var port = config.DashboardPort;
        Port = port;
        _adminToken = await GetOrCreateAdminTokenAsync(db);
        _adminPasswordSet = !string.IsNullOrEmpty(await db.GetSettingAsync("DashboardAdminPasswordHash", ""));

        var contentRoot = AppContext.BaseDirectory;
        var wwwroot = Path.Combine(contentRoot, "wwwroot");
        Directory.CreateDirectory(wwwroot);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            WebRootPath = wwwroot,
            EnvironmentName = "Production"
        });

        var bindAddress = config.EnableRemoteDashboard
            ? (string.IsNullOrWhiteSpace(config.DashboardBindAddress) ? "0.0.0.0" : config.DashboardBindAddress)
            : "127.0.0.1";
        builder.WebHost.UseSetting("urls", $"http://{bindAddress}:{port}");
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter((provider, category, logLevel) => false);

        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(tracker);
        builder.Services.AddSingleton(enforcer);
        builder.Services.AddSingleton(scheduler);

        _app = builder.Build();

        _app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api") &&
                !ctx.Request.Path.StartsWithSegments("/api/auth") &&
                _adminPasswordSet)
            {
                var auth = RequireWriteAccess(ctx);
                if (auth != null)
                {
                    await auth.ExecuteAsync(ctx);
                    return;
                }
            }

            await next();
        });

        var fileOpts = new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(wwwroot)
        };
        _app.UseStaticFiles(fileOpts);

        var discovery = new DiscoveryService();
        var notifier = new NotificationService(db);
        MapApiEndpoints(_app, db, tracker, enforcer, scheduler, discovery, notifier, emailService);

        await _app.StartAsync();
    }

    private static void MapApiEndpoints(WebApplication app, UsageDatabase db, WindowTracker tracker,
        LimitEnforcer enforcer, SchedulerService scheduler, DiscoveryService discovery,
        NotificationService notifier, EmailService emailService)
    {
        app.MapGet("/", async ctx =>
        {
            ctx.Response.Redirect("/index.html");
        });

        app.MapPost("/api/auth/login", async (HttpContext ctx) =>
        {
            string password = "";
            if (ctx.Request.ContentLength > 0)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (doc.RootElement.TryGetProperty("password", out var pw))
                    password = pw.GetString() ?? "";
            }

            var storedHash = await db.GetSettingAsync("DashboardAdminPasswordHash", "");
            if (string.IsNullOrEmpty(storedHash))
            {
                if (!IsLocalRequest(ctx))
                    return Results.Json(new { error = "Admin password has not been set locally yet." }, statusCode: StatusCodes.Status403Forbidden);
                return Results.Ok(new { token = _adminToken, passwordSet = false });
            }

            if (!PasswordHasher.Verify(password, storedHash))
                return Results.Json(new { error = "Invalid admin password." }, statusCode: StatusCodes.Status401Unauthorized);

            return Results.Ok(new { token = _adminToken, passwordSet = true });
        });

        app.MapGet("/api/auth/status", () =>
            Results.Ok(new { passwordSet = _adminPasswordSet }));

        app.MapPost("/api/auth/password", async (HttpContext ctx) =>
        {
            string currentPassword = "";
            string newPassword = "";
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = doc.RootElement;
            if (root.TryGetProperty("currentPassword", out var cp))
                currentPassword = cp.GetString() ?? "";
            if (root.TryGetProperty("newPassword", out var np))
                newPassword = np.GetString() ?? "";

            if (newPassword.Length is < 8 or > 128)
                return Results.BadRequest(new { error = "Admin password must be 8-128 characters." });

            var storedHash = await db.GetSettingAsync("DashboardAdminPasswordHash", "");
            if (!string.IsNullOrEmpty(storedHash))
            {
                var auth = RequireWriteAccess(ctx);
                if (auth != null && !PasswordHasher.Verify(currentPassword, storedHash))
                    return auth;
            }
            else if (!IsLocalRequest(ctx))
            {
                return Results.Json(new { error = "Initial admin password must be set on the child PC." }, statusCode: StatusCodes.Status403Forbidden);
            }

            await db.SetSettingAsync("DashboardAdminPasswordHash", PasswordHasher.Hash(newPassword));
            _adminPasswordSet = true;
            return Results.Ok(new { status = "password_set", token = _adminToken });
        });

        app.MapGet("/api/usage/today", async () =>
            Results.Json(await db.GetTodayUsageAsync(), JsonOpts));

        app.MapGet("/api/usage/history", async (int days = 7, string? from = null, string? to = null) =>
        {
            DateTime startDate;
            DateTime endDate;

            if (!string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to))
            {
                if (!DateTime.TryParse(from, out startDate) || !DateTime.TryParse(to, out endDate))
                    return Results.BadRequest(new { error = "Invalid date range." });
                startDate = startDate.Date;
                endDate = endDate.Date;
            }
            else
            {
                days = Math.Clamp(days, 1, 366);
                endDate = DateTime.Today;
                startDate = endDate.AddDays(-days + 1);
            }

            if (startDate > endDate)
                return Results.BadRequest(new { error = "Start date must be before end date." });
            if ((endDate - startDate).TotalDays > 366)
                return Results.BadRequest(new { error = "History range is limited to 366 days." });

            return Results.Json(await db.GetUsageRangeAsync(startDate, endDate), JsonOpts);
        });

        app.MapDelete("/api/usage/history", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            await db.ClearUsageHistoryAsync();
            enforcer.ClearExceeded();
            return Results.Ok(new { status = "cleared" });
        });

        app.MapGet("/api/live", () =>
        {
            var countdownApps = new List<object>();
            return Results.Json(new
            {
                currentApp = tracker.CurrentAppName ?? "None",
                currentProcess = tracker.CurrentProcessName ?? "None",
                isTracking = tracker.CurrentAppName != null
            }, JsonOpts);
        });

        app.MapGet("/api/apps", () =>
            Results.Json(tracker.KnownApps, JsonOpts));

        app.MapPost("/api/apps", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = doc.RootElement;
            var procName = Clean(root.GetProperty("processName").GetString());
            var appName = Clean(root.GetProperty("appName").GetString());
            if (!IsValidProcessName(procName) || !IsValidAppName(appName))
                return Results.BadRequest(new { error = "Invalid process or app name." });
            tracker.AddKnownApp(procName, appName);
            await db.SaveAppMappingAsync(procName, appName);
            return Results.Ok();
        });

        app.MapDelete("/api/apps/{processName}", async (HttpContext ctx, string processName) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            processName = Clean(processName);
            if (!IsValidProcessName(processName))
                return Results.BadRequest(new { error = "Invalid process name." });

            // Look up the app name before deleting mapping
            var appName = tracker.KnownApps.TryGetValue(processName, out var name)
                ? name : Path.GetFileNameWithoutExtension(processName);
            enforcer.ClearExceeded(appName);

            await db.DeleteAppMappingAsync(processName);
            // Rebuild tracker mappings from config + remaining dynamic
            var config = Program.GetConfig();
            tracker.LoadKnownApps(config.KnownApps);
            var mappings = await db.GetAppMappingsAsync();
            foreach (var m in mappings)
                tracker.AddKnownApp(m.ProcessName, m.AppName);
            return Results.Ok();
        });

        app.MapGet("/api/mappings", async () =>
            Results.Json(await db.GetAppMappingsAsync(), JsonOpts));

        app.MapGet("/api/discover", () =>
        {
            var apps = discovery.ScanForApps();
            var knownKeys = new HashSet<string>(tracker.KnownApps.Keys, StringComparer.OrdinalIgnoreCase);
            var result = apps.Where(a => !knownKeys.Contains(a.ProcessName)).ToList();
            return Results.Json(result, JsonOpts);
        });

        app.MapGet("/api/processes", () =>
        {
            try
            {
                var knownKeys = new HashSet<string>(tracker.KnownApps.Keys, StringComparer.OrdinalIgnoreCase);
                var result = System.Diagnostics.Process.GetProcesses()
                    .Where(p =>
                    {
                        try
                        {
                            var name = p.ProcessName + ".exe";
                            return !string.IsNullOrEmpty(p.MainWindowTitle) &&
                                   !knownKeys.Contains(name);
                        }
                        catch { return false; }
                    })
                    .Select(p =>
                    {
                        try { return new { name = p.ProcessName + ".exe", title = p.MainWindowTitle ?? "", pid = p.Id }; }
                        catch { return null; }
                    })
                    .Where(x => x != null)
                    .DistinctBy(x => x!.name)
                    .ToList();
                return Results.Json(result, JsonOpts);
            }
            catch { return Results.Json(new List<object>(), JsonOpts); }
        });

        app.MapGet("/api/limits", async () =>
            Results.Json(await db.GetLimitRulesAsync(), JsonOpts));

        app.MapPost("/api/limits", async (HttpContext ctx, AppLimitRule limit) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            limit.AppName = Clean(limit.AppName);
            if (!IsValidAppName(limit.AppName) || limit.DailyMaxMinutes is < 1 or > 1440)
                return Results.BadRequest(new { error = "Limit must have a valid app name and 1-1440 minutes." });
            enforcer.ClearExceeded(limit.AppName);
            await db.SaveLimitRuleAsync(limit);
            return Results.Ok();
        });

        app.MapDelete("/api/limits/{appName}", async (HttpContext ctx, string appName) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            appName = Clean(appName);
            if (!IsValidAppName(appName))
                return Results.BadRequest(new { error = "Invalid app name." });
            enforcer.ClearExceeded(appName);
            await db.DeleteLimitRuleAsync(appName);
            return Results.Ok();
        });

        app.MapGet("/api/schedule", async () =>
            Results.Json(await db.GetScheduleRulesAsync(), JsonOpts));

        app.MapPost("/api/schedule", async (HttpContext ctx, ScheduleRule rule) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            if (!NormalizeScheduleRule(rule))
                return Results.BadRequest(new { error = "Invalid schedule rule." });
            await db.SaveScheduleRuleAsync(rule);
            scheduler.InvalidateCache();
            return Results.Ok();
        });

        app.MapPut("/api/schedule", async (HttpContext ctx, ScheduleRule rule) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            if (!NormalizeScheduleRule(rule))
                return Results.BadRequest(new { error = "Invalid schedule rule." });
            await db.UpdateScheduleRuleAsync(rule);
            scheduler.InvalidateCache();
            return Results.Ok();
        });

        app.MapDelete("/api/schedule/{id}", async (HttpContext ctx, int id) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            if (id <= 0)
                return Results.BadRequest(new { error = "Invalid schedule id." });
            await db.DeleteScheduleRuleAsync(id);
            scheduler.InvalidateCache();
            return Results.Ok();
        });

        app.MapGet("/api/settings", async (HttpContext ctx) =>
        {
            var killDelay = await db.GetKillDelayAsync();
            var showWarning = await db.GetShowWarningAsync();
            var webhookUrl = await db.GetSettingAsync("WebhookUrl", "");
            var warningMessage = await db.GetWarningMessageAsync();
            var emailAddress = await db.GetSettingAsync("EmailAddress", "");
            var emailAllowedSender = await db.GetSettingAsync("EmailAllowedSender", emailAddress);
            var emailNotifyEnabled = await db.GetSettingAsync("EmailNotifyEnabled", "false");
            var emailStartNotifyEnabled = await db.GetSettingAsync("EmailStartNotifyEnabled", "false");
            var emailControlEnabled = await db.GetSettingAsync("EmailControlEnabled", "false");
            var config = Program.GetConfig();
            var hostname = Environment.MachineName;
            var localIps = System.Net.Dns.GetHostEntry(hostname).AddressList
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToArray();
            return Results.Json(new
            {
                killDelay,
                showWarning,
                webhookUrl,
                warningMessage,
                emailAddress,
                emailAllowedSender,
                hostname,
                localIps,
                emailNotifyEnabled = emailNotifyEnabled == "true",
                emailStartNotifyEnabled = emailStartNotifyEnabled == "true",
                emailControlEnabled = emailControlEnabled == "true",
                remoteDashboardEnabled = config.EnableRemoteDashboard,
                adminPasswordSet = _adminPasswordSet,
                dashboardTokenRequired = _adminPasswordSet || !IsLocalRequest(ctx)
            }, JsonOpts);
        });

        app.MapPost("/api/settings", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = doc.RootElement;
            if (root.TryGetProperty("killDelay", out var kd))
            {
                var killDelay = kd.GetInt32();
                if (killDelay is < 5 or > 300)
                    return Results.BadRequest(new { error = "Kill delay must be between 5 and 300 seconds." });
                await db.SetKillDelayAsync(killDelay);
            }
            if (root.TryGetProperty("showWarning", out var sw))
                await db.SetShowWarningAsync(sw.GetBoolean());
            if (root.TryGetProperty("webhookUrl", out var wh))
            {
                var webhookUrl = Clean(wh.GetString());
                if (!string.IsNullOrEmpty(webhookUrl) && !IsHttpUrl(webhookUrl))
                    return Results.BadRequest(new { error = "Webhook URL must be http or https." });
                await db.SetSettingAsync("WebhookUrl", webhookUrl);
                notifier.InvalidateCache();
            }
            if (root.TryGetProperty("warningMessage", out var wm))
            {
                var message = Clean(wm.GetString());
                if (message.Length > 200)
                    return Results.BadRequest(new { error = "Warning message is too long." });
                await db.SetWarningMessageAsync(message);
            }
            if (root.TryGetProperty("emailAddress", out var ea))
            {
                var emailAddress = Clean(ea.GetString());
                if (!string.IsNullOrEmpty(emailAddress) && !IsValidEmail(emailAddress))
                    return Results.BadRequest(new { error = "Invalid email address." });
                await db.SetSettingAsync("EmailAddress", emailAddress);
            }
            if (root.TryGetProperty("emailPassword", out var ep))
            {
                var password = (ep.GetString() ?? "").Replace(" ", "");
                if (password.Length > 0)
                    await db.SetSettingAsync("EmailPassword", SecretProtector.Protect(password));
            }
            if (root.TryGetProperty("emailAllowedSender", out var eas))
            {
                var allowedSender = Clean(eas.GetString());
                if (!string.IsNullOrEmpty(allowedSender) && !IsValidEmailList(allowedSender))
                    return Results.BadRequest(new { error = "Invalid allowed sender email address list." });
                await db.SetSettingAsync("EmailAllowedSender", allowedSender);
            }
            if (root.TryGetProperty("emailNotifyEnabled", out var en))
                await db.SetSettingAsync("EmailNotifyEnabled", en.GetBoolean() ? "true" : "false");
            if (root.TryGetProperty("emailStartNotifyEnabled", out var esn))
                await db.SetSettingAsync("EmailStartNotifyEnabled", esn.GetBoolean() ? "true" : "false");
            if (root.TryGetProperty("emailControlEnabled", out var ec))
                await db.SetSettingAsync("EmailControlEnabled", ec.GetBoolean() ? "true" : "false");
            // Always reload email config after save
            await emailService.LoadSettingsAsync();
            if (emailService.IsEnabled) emailService.StartPolling(); else emailService.StopPolling();
            return Results.Ok();
        });

        app.MapPost("/api/settings/webhook-test", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            await notifier.TestWebhookAsync();
            return Results.Ok(new { status = "test_sent" });
        });

        app.MapPost("/api/settings/email-test", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            string? emailAddress = null;
            string? emailPassword = null;
            if (ctx.Request.ContentLength > 0)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var root = doc.RootElement;
                if (root.TryGetProperty("emailAddress", out var ea))
                    emailAddress = ea.GetString();
                if (root.TryGetProperty("emailPassword", out var ep))
                    emailPassword = ep.GetString();
            }

            var result = await emailService.TestEmailAsync(emailAddress, emailPassword);
            if (result == null)
                return Results.Ok(new { status = "test_sent" });
            return Results.Json(new { status = "error", error = result }, statusCode: 500);
        });

        app.MapPost("/api/settings/email-start-test", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            var result = await emailService.TestStartEmailAsync();
            if (result == null)
                return Results.Ok(new { status = "test_sent" });
            return Results.Json(new { status = "error", error = result }, statusCode: 500);
        });

        app.MapPost("/api/settings/email-start-reset", (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            emailService.ResetStartEmailMarkers();
            return Results.Ok(new { status = "reset" });
        });

        app.MapGet("/api/logs", (HttpContext ctx, int count = 200) =>
        {
            count = Math.Clamp(count, 1, 1000);
            return Results.Json(Logger.Instance.GetRecent(count));
        });

        app.MapDelete("/api/logs", (HttpContext ctx) =>
        {
            Logger.Instance.Clear();
            return Results.Ok(new { status = "cleared" });
        });

        app.MapPost("/api/shutdown", (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                Program.Shutdown();
            });
            return Results.Ok(new { status = "shutting_down" });
        });

        app.MapGet("/api/alerts/stream", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.Append("Content-Type", "text/event-stream");
            ctx.Response.Headers.Append("Cache-Control", "no-cache");
            ctx.Response.Headers.Append("Connection", "keep-alive");
            ctx.Response.Headers.Append("X-Accel-Buffering", "no");

            var ct = ctx.RequestAborted;

            void SendAlert(string type, string appName, int? extra = null)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var data = extra.HasValue
                        ? $"{{\"type\":\"{type}\",\"appName\":\"{EscapeJson(appName)}\",\"value\":{extra}}}"
                        : $"{{\"type\":\"{type}\",\"appName\":\"{EscapeJson(appName)}\"}}";
                    ctx.Response.WriteAsync($"event: {type}\ndata: {data}\n\n", ct).Wait();
                    ctx.Response.Body.FlushAsync(ct).Wait();
                }
                catch { }
            }

            enforcer.OnBreachAlert += (app, delay) => SendAlert("breach", app, delay);
            enforcer.OnCountdownTick += (app, secs) => SendAlert("countdown", app, secs);
            enforcer.OnAppKilled += (app) => SendAlert("killed", app);
            enforcer.OnAppTerminatedBySchedule += (app) => SendAlert("schedule_kill", app);

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) { }
        });
    }

    private static async Task<string> GetOrCreateAdminTokenAsync(UsageDatabase db)
    {
        var token = await db.GetSettingAsync("DashboardAdminToken", "");
        if (!string.IsNullOrWhiteSpace(token))
            return token;

        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await db.SetSettingAsync("DashboardAdminToken", token);
        return token;
    }

    private static IResult? RequireWriteAccess(HttpContext ctx)
    {
        if (!_adminPasswordSet && IsLocalRequest(ctx))
            return null;

        if (IsLocalRequest(ctx))
        {
            var setupToken = ctx.Request.Headers["X-Admin-Token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(setupToken) && !_adminPasswordSet)
                return null;
        }

        var provided = ctx.Request.Headers["X-Admin-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(provided))
            provided = ctx.Request.Query["token"].FirstOrDefault();
        var providedBytes = System.Text.Encoding.UTF8.GetBytes(provided ?? "");
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(_adminToken);
        if (!string.IsNullOrWhiteSpace(_adminToken) &&
            providedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
            return null;

        return Results.Json(new { error = "Admin token required." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    private static bool IsLocalRequest(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip == null || System.Net.IPAddress.IsLoopback(ip);
    }

    private static string Clean(string? value) => (value ?? "").Trim();

    private static bool IsValidAppName(string value) =>
        value.Length is > 0 and <= 120 && !value.Any(char.IsControl);

    private static bool IsValidProcessName(string value) =>
        value.Length is > 4 and <= 260 &&
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(value).Equals(value, StringComparison.OrdinalIgnoreCase) &&
        !value.Any(char.IsControl);

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsValidEmail(string value) =>
        MimeKit.MailboxAddress.TryParse(value, out _);

    private static bool IsValidEmailList(string value)
    {
        var emails = value.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return emails.Length > 0 && emails.All(IsValidEmail);
    }

    private static bool NormalizeScheduleRule(ScheduleRule rule)
    {
        rule.DayOfWeek = Clean(rule.DayOfWeek);
        rule.StartTime = Clean(rule.StartTime);
        rule.EndTime = Clean(rule.EndTime);

        if (!AllowedDays.Contains(rule.DayOfWeek))
            return false;
        if (!TimeSpan.TryParse(rule.StartTime, out _) ||
            !TimeSpan.TryParse(rule.EndTime, out _))
            return false;

        return true;
    }

    private static readonly HashSet<string> AllowedDays = new(StringComparer.OrdinalIgnoreCase)
    {
        "Weekday", "Weekend", "Everyday",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
    };

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    public static async Task StopAsync()
    {
        if (_app != null)
            await _app.StopAsync();
    }
}
