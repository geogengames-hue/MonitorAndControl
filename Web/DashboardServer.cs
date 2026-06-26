using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
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
    private static readonly ConcurrentDictionary<string, AuthAttemptState> AuthAttempts = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxFailedLoginAttempts = 5;
    private static readonly TimeSpan AuthLockoutDuration = TimeSpan.FromMinutes(5);
    private const string AdminCookieName = "DeviceMonAdminToken";
    public static Action<string>? LoginLockoutDetected { get; set; }

    public static async Task StartAsync(UsageDatabase db, WindowTracker tracker, LimitEnforcer enforcer,
        SchedulerService scheduler, UsageTracker usageTracker, EmailService emailService, AppConfig config)
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
                !ctx.Request.Path.StartsWithSegments("/api/auth"))
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

        _app.Use(async (ctx, next) =>
        {
            if (ShouldGateDashboardAsset(ctx))
            {
                ctx.Response.Redirect("/login.html");
                return;
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
        MapApiEndpoints(_app, db, tracker, enforcer, scheduler, usageTracker, discovery, notifier, emailService);

        await _app.StartAsync();
    }

    private static void MapApiEndpoints(WebApplication app, UsageDatabase db, WindowTracker tracker,
        LimitEnforcer enforcer, SchedulerService scheduler, UsageTracker usageTracker, DiscoveryService discovery,
        NotificationService notifier, EmailService emailService)
    {
        app.MapGet("/", (HttpContext ctx) =>
        {
            ctx.Response.Redirect("/index.html");
            return Task.CompletedTask;
        });

        app.MapPost("/api/auth/login", async (HttpContext ctx) =>
        {
            var rateLimit = CheckLoginRateLimit(ctx);
            if (rateLimit != null)
                return rateLimit;

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
                if (!IsLocalSetupAllowed(ctx))
                    return Results.Json(new { error = "Admin password has not been set locally yet." }, statusCode: StatusCodes.Status403Forbidden);
                return Results.Ok(new { passwordSet = false });
            }

            if (password.Length > 128)
                return Results.BadRequest(new { error = "Admin password is too long." });

            if (!PasswordHasher.Verify(password, storedHash))
            {
                RegisterFailedLogin(ctx);
                return Results.Json(new { error = "Invalid admin password." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            ResetLoginRateLimit(ctx);
            await RotateAdminTokenAsync(db);
            SetAdminCookie(ctx);
            return Results.Ok(new { passwordSet = true });
        });

        app.MapGet("/api/auth/status", () =>
            Results.Ok(new { passwordSet = _adminPasswordSet }));

        app.MapPost("/api/auth/logout", (HttpContext ctx) =>
        {
            ClearAdminCookie(ctx);
            return Results.Ok(new { status = "logged_out" });
        });

        app.MapGet("/api/health", () =>
            Results.Ok(new
            {
                status = "ok",
                machine = Environment.MachineName,
                timestamp = DateTimeOffset.Now,
                runtime = Program.GetRuntimeHealth(Program.GetConfig(), db, emailService)
            }));

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
                if (auth != null)
                    return auth;
                if (!PasswordHasher.Verify(currentPassword, storedHash))
                    return Results.Json(new { error = "Current admin password is incorrect." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            else if (!IsLocalSetupAllowed(ctx))
            {
                return Results.Json(
                    new { error = "Initial admin password must be set from a trusted local dashboard before remote access is enabled." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            await db.SetSettingAsync("DashboardAdminPasswordHash", PasswordHasher.Hash(newPassword));
            _adminPasswordSet = true;
            await RotateAdminTokenAsync(db);
            SetAdminCookie(ctx);
            return Results.Ok(new { status = "password_set" });
        });

        app.MapPost("/api/auth/token/rotate", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            await RotateAdminTokenAsync(db);
            SetAdminCookie(ctx);
            Logger.Instance.Warn("Dashboard admin token rotated");
            return Results.Ok(new { status = "rotated" });
        });

        app.MapGet("/api/usage/today", async () =>
            Results.Json(await usageTracker.GetTodayUsageIncludingPendingAsync(), JsonOpts));

        app.MapGet("/api/usage/groups/today", async () =>
            Results.Json(await usageTracker.GetTodayGroupUsageIncludingPendingAsync(), JsonOpts));

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

        app.MapGet("/api/usage/groups/history", async (int days = 7, string? from = null, string? to = null) =>
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
            return Results.Json(await db.GetLimitGroupUsageRangeAsync(startDate, endDate), JsonOpts);
        });

        app.MapDelete("/api/usage/history", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            await db.ClearUsageHistoryAsync();
            enforcer.ClearExceeded();
            return Results.Ok(new { status = "cleared" });
        });

        app.MapPost("/api/actions/pause", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            var minutes = 15;
            if (ctx.Request.ContentLength > 0)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (doc.RootElement.TryGetProperty("minutes", out var mins))
                    minutes = mins.GetInt32();
            }

            if (minutes is < 1 or > 240)
                return Results.BadRequest(new { error = "Pause duration must be between 1 and 240 minutes." });

            var pausedUntil = enforcer.PauseFor(TimeSpan.FromMinutes(minutes));
            return Results.Ok(new { status = "paused", pausedUntil });
        });

        app.MapPost("/api/actions/resume", (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            enforcer.Resume();
            return Results.Ok(new { status = "resumed" });
        });

        app.MapPost("/api/actions/reset-today", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            await usageTracker.ResetTodayAsync();
            enforcer.ClearExceeded();
            return Results.Ok(new { status = "today_reset" });
        });

        app.MapPost("/api/actions/block-all", (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            var killed = enforcer.KillRunningTrackedApps();
            return Results.Ok(new { status = "blocked", killed });
        });

        app.MapGet("/api/live", () =>
        {
            var trackingSuspended = tracker.IsUsageTrackingSuspended(out var trackingState);
            return Results.Json(new
            {
                currentApp = tracker.CurrentAppName ?? "None",
                currentProcess = tracker.CurrentProcessName ?? "None",
                isTracking = tracker.CurrentAppName != null,
                enforcementPaused = enforcer.IsPaused,
                pausedUntil = enforcer.PausedUntil,
                trackingSuspended,
                trackingState,
                idleSeconds = (long)tracker.GetIdleDuration().TotalSeconds,
                pauseWhenIdle = tracker.PauseWhenIdle,
                idleThresholdMinutes = tracker.IdleThresholdMinutes,
                diagnostics = tracker.GetTrackingDiagnostics()
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
            var countInBackground = root.TryGetProperty("countInBackground", out var backgroundElement) &&
                backgroundElement.ValueKind is JsonValueKind.True;
            var ignoreOverlayFocus = root.TryGetProperty("ignoreOverlayFocus", out var overlayElement) &&
                overlayElement.ValueKind is JsonValueKind.True;
            if (!IsValidProcessName(procName) || !IsValidAppName(appName))
                return Results.BadRequest(new { error = "Invalid process or app name." });
            tracker.AddKnownApp(procName, appName, countInBackground, ignoreOverlayFocus);
            await db.SaveAppMappingAsync(procName, appName, countInBackground, ignoreOverlayFocus);
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
            await db.DeleteAppMappingAsync(processName);
            // Rebuild tracker mappings from config + remaining dynamic
            var config = Program.GetConfig();
            tracker.LoadKnownApps(config.KnownApps);
            var mappings = await db.GetAppMappingsAsync();
            foreach (var m in mappings)
                tracker.AddKnownApp(m.ProcessName, m.AppName, m.CountInBackground, m.IgnoreOverlayFocus);
            var appRemoved = tracker.GetProcessNamesForApp(appName).Length == 0;
            if (appRemoved)
            {
                enforcer.ClearExceeded(appName);
                await db.DeleteAppUsageAsync(appName);
                await db.DeleteLimitRuleAsync(appName);
                await db.RemoveAppFromLimitGroupsAsync(appName);
                await usageTracker.ReloadLimitGroupsAsync();
            }
            return Results.Ok(new { appRemoved });
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
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var result = new List<object>();
                foreach (var process in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        var name = process.ProcessName + ".exe";
                        if (string.IsNullOrEmpty(process.MainWindowTitle) || knownKeys.Contains(name))
                            continue;
                        if (!seen.Add(name))
                            continue;

                        result.Add(new { name, title = process.MainWindowTitle ?? "", pid = process.Id });
                    }
                    catch (Exception ex)
                    {
                        Logger.Instance.Error($"Failed to inspect process {process.Id}: {ex.Message}");
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                return Results.Json(result, JsonOpts);
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"Failed to enumerate running processes: {ex.Message}");
                return Results.Json(new List<object>(), JsonOpts);
            }
        });

        app.MapGet("/api/limits", async () =>
            Results.Json(await db.GetLimitRulesAsync(), JsonOpts));

        app.MapGet("/api/limit-groups", async () =>
        {
            var groups = await usageTracker.GetLimitGroupsIncludingPendingAsync();
            return Results.Json(groups.Select(group => new
            {
                group.Id,
                group.Name,
                group.DailyMaxMinutes,
                group.Enabled,
                group.AppNames,
                group.TodaySeconds
            }), JsonOpts);
        });

        app.MapPost("/api/limit-groups", async (HttpContext ctx, AppLimitGroup group) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            group.Name = Clean(group.Name);
            group.AppNames = group.AppNames.Select(Clean)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!IsValidAppName(group.Name) || group.DailyMaxMinutes is < 1 or > 1440 || group.AppNames.Count == 0 ||
                group.AppNames.Any(name => !IsValidAppName(name)))
                return Results.BadRequest(new { error = "A group requires a valid name, 1-1440 minutes, and at least one valid app." });

            var groups = await db.GetLimitGroupsAsync();
            if (group.Id > 0 && groups.All(existing => existing.Id != group.Id))
                return Results.NotFound(new { error = "Limit group not found." });
            if (groups.Any(existing => existing.Id != group.Id && existing.Name.Equals(group.Name, StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest(new { error = "A group with this name already exists." });
            var duplicateMember = groups
                .Where(existing => existing.Id != group.Id)
                .SelectMany(existing => existing.AppNames)
                .FirstOrDefault(member => group.AppNames.Contains(member, StringComparer.OrdinalIgnoreCase));
            if (duplicateMember != null)
                return Results.BadRequest(new { error = $"{duplicateMember} already belongs to another group." });

            var previousMembers = groups.FirstOrDefault(existing => existing.Id == group.Id)?.AppNames
                ?? new List<string>();
            if (group.Id > 0) enforcer.ClearGroup(group.Id);
            await db.SaveLimitGroupAsync(group);
            await usageTracker.ReloadLimitGroupsAsync();
            foreach (var appName in previousMembers.Concat(group.AppNames).Distinct(StringComparer.OrdinalIgnoreCase))
                enforcer.ClearExceeded(appName);
            return Results.Ok();
        });

        app.MapDelete("/api/limit-groups/{id:int}", async (HttpContext ctx, int id) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;
            var group = (await db.GetLimitGroupsAsync()).FirstOrDefault(item => item.Id == id);
            if (group == null) return Results.NotFound();
            enforcer.ClearGroup(id);
            await db.DeleteLimitGroupAsync(id);
            await usageTracker.ReloadLimitGroupsAsync();
            foreach (var appName in group.AppNames) enforcer.ClearExceeded(appName);
            return Results.Ok();
        });

        app.MapGet("/api/bonus/today", async () =>
            Results.Json(await db.GetTodayBonusTimeAsync(), JsonOpts));

        app.MapPost("/api/bonus", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            string appName = "";
            var minutes = 0;
            if (ctx.Request.ContentLength > 0)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var root = doc.RootElement;
                if (root.TryGetProperty("appName", out var app))
                    appName = Clean(app.GetString());
                if (root.TryGetProperty("minutes", out var mins))
                    minutes = mins.GetInt32();
            }

            if (!IsValidAppName(appName) || minutes is < 1 or > 240)
                return Results.BadRequest(new { error = "Bonus time requires a valid app name and 1-240 minutes." });

            var limits = await db.GetLimitRulesAsync();
            if (!limits.Any(l => l.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest(new { error = "Bonus time can only be granted to apps with a limit." });

            var totalBonusMinutes = await db.AddTodayBonusMinutesAsync(appName, minutes);
            enforcer.ClearExceeded(appName);
            Logger.Instance.Warn($"Bonus time granted: {appName} +{minutes} min today ({totalBonusMinutes} min total)");
            return Results.Ok(new { status = "granted", appName, bonusMinutes = totalBonusMinutes });
        });

        app.MapPost("/api/bonus/until-bedtime", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            string appName = "";
            if (ctx.Request.ContentLength > 0)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (doc.RootElement.TryGetProperty("appName", out var app))
                    appName = Clean(app.GetString());
            }

            if (!IsValidAppName(appName))
                return Results.BadRequest(new { error = "A valid app name is required." });

            var limits = await db.GetLimitRulesAsync();
            var limit = limits.FirstOrDefault(l => l.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));
            if (limit == null)
                return Results.BadRequest(new { error = "Bonus time can only be granted to apps with a limit." });

            var now = DateTime.Now;
            var rules = await scheduler.GetRulesAsync();
            var allowedUntil = SchedulerService.GetCurrentAllowedWindowEnd(rules, appName, now)
                ?? now.Date.AddDays(1);
            if (allowedUntil <= now)
                return Results.BadRequest(new { error = "No remaining allowed time today." });

            var usageSeconds = await db.GetAppTodaySecondsAsync(appName);
            var currentBonus = await db.GetTodayBonusMinutesAsync(appName);
            var currentAllowanceMinutes = limit.DailyMaxMinutes + currentBonus;
            var desiredAllowanceMinutes = (int)Math.Ceiling((usageSeconds + (allowedUntil - now).TotalSeconds) / 60.0);
            var minutesToAdd = Math.Clamp(desiredAllowanceMinutes - currentAllowanceMinutes, 1, 720);

            var totalBonusMinutes = await db.AddTodayBonusMinutesAsync(appName, minutesToAdd);
            enforcer.ClearExceeded(appName);
            Logger.Instance.Warn($"Bonus time granted until bedtime: {appName} +{minutesToAdd} min today ({totalBonusMinutes} min total, until {allowedUntil:g})");
            return Results.Ok(new
            {
                status = "granted",
                appName,
                addedMinutes = minutesToAdd,
                bonusMinutes = totalBonusMinutes,
                allowedUntil
            });
        });

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
            var emailBreachNotifyEnabled = await db.GetSettingAsync("EmailBreachNotifyEnabled", emailNotifyEnabled);
            var emailKillNotifyEnabled = await db.GetSettingAsync("EmailKillNotifyEnabled", emailNotifyEnabled);
            var emailStartNotifyEnabled = await db.GetSettingAsync("EmailStartNotifyEnabled", "false");
            var emailControlEnabled = await db.GetSettingAsync("EmailControlEnabled", "false");
            var emailDeviceId = await db.GetSettingAsync("EmailDeviceId", Environment.MachineName);
            var uiLanguage = await db.GetSettingAsync("UiLanguage", "en");
            var pauseTrackingWhenIdle = await db.GetSettingAsync("PauseTrackingWhenIdle", "false");
            var idleThresholdMinutes = await db.GetSettingAsync("IdleThresholdMinutes", "10");
            var summaryEnabled = await db.GetSettingAsync("SummaryEnabled", "false");
            var summaryFrequency = await db.GetSettingAsync("SummaryFrequency", "weekly");
            var summaryTime = await db.GetSettingAsync("SummaryTime", "18:00");
            var summaryWeeklyDay = await db.GetSettingAsync("SummaryWeeklyDay", "0");
            var summaryMonthlyDay = await db.GetSettingAsync("SummaryMonthlyDay", "1");
            var tamperAlertsEnabled = await db.GetSettingAsync("TamperAlertsEnabled", "false");
            var config = Program.GetConfig();
            var (hotKeyModifiers, hotKeyKey) = await Program.GetDashboardHotKeySettings(config);
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
                autoStart = Program.GetAutoStart(),
                emailNotifyEnabled = emailNotifyEnabled == "true",
                emailBreachNotifyEnabled = emailBreachNotifyEnabled == "true",
                emailKillNotifyEnabled = emailKillNotifyEnabled == "true",
                emailStartNotifyEnabled = emailStartNotifyEnabled == "true",
                emailControlEnabled = emailControlEnabled == "true",
                emailDeviceId = EmailService.NormalizeDeviceId(emailDeviceId),
                uiLanguage,
                pauseTrackingWhenIdle = pauseTrackingWhenIdle == "true",
                idleThresholdMinutes = int.TryParse(idleThresholdMinutes, out var idleMinutes) ? idleMinutes : 10,
                summaryEnabled = summaryEnabled == "true",
                summaryFrequency,
                summaryTime,
                summaryWeeklyDay = int.TryParse(summaryWeeklyDay, out var weeklyDay) ? weeklyDay : 0,
                summaryMonthlyDay = int.TryParse(summaryMonthlyDay, out var monthlyDay) ? monthlyDay : 1,
                tamperAlertsEnabled = tamperAlertsEnabled == "true",
                hotKeyModifiers,
                hotKeyKey,
                hotKey = $"{hotKeyModifiers}+{hotKeyKey}",
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
            {
                var legacyValue = en.GetBoolean() ? "true" : "false";
                await db.SetSettingAsync("EmailNotifyEnabled", legacyValue);
                if (!root.TryGetProperty("emailBreachNotifyEnabled", out _))
                    await db.SetSettingAsync("EmailBreachNotifyEnabled", legacyValue);
                if (!root.TryGetProperty("emailKillNotifyEnabled", out _))
                    await db.SetSettingAsync("EmailKillNotifyEnabled", legacyValue);
            }
            if (root.TryGetProperty("emailBreachNotifyEnabled", out var breachNotify))
                await db.SetSettingAsync("EmailBreachNotifyEnabled", breachNotify.GetBoolean() ? "true" : "false");
            if (root.TryGetProperty("emailKillNotifyEnabled", out var killNotify))
                await db.SetSettingAsync("EmailKillNotifyEnabled", killNotify.GetBoolean() ? "true" : "false");
            if (root.TryGetProperty("emailStartNotifyEnabled", out var esn))
                await db.SetSettingAsync("EmailStartNotifyEnabled", esn.GetBoolean() ? "true" : "false");
            if (root.TryGetProperty("emailControlEnabled", out var ec))
                await db.SetSettingAsync("EmailControlEnabled", ec.GetBoolean() ? "true" : "false");
            if (root.TryGetProperty("emailDeviceId", out var edi))
            {
                var deviceId = EmailService.NormalizeDeviceId(Clean(edi.GetString()));
                if (deviceId.Length is < 1 or > 40)
                    return Results.BadRequest(new { error = "Email device ID must be 1-40 letters, numbers, dots, dashes, or underscores." });
                await db.SetSettingAsync("EmailDeviceId", deviceId);
            }
            if (root.TryGetProperty("uiLanguage", out var lang))
            {
                var language = Clean(lang.GetString()).ToLowerInvariant();
                if (!AllowedLanguages.Contains(language))
                    return Results.BadRequest(new { error = "Unsupported language." });
                await db.SetSettingAsync("UiLanguage", language);
            }
            if (root.TryGetProperty("pauseTrackingWhenIdle", out var pauseIdle))
                await db.SetSettingAsync("PauseTrackingWhenIdle", pauseIdle.GetBoolean() ? "true" : "false");
            if (root.TryGetProperty("idleThresholdMinutes", out var idleThreshold))
            {
                var minutes = idleThreshold.GetInt32();
                if (minutes is < 1 or > 240)
                    return Results.BadRequest(new { error = "Idle threshold must be between 1 and 240 minutes." });
                await db.SetSettingAsync("IdleThresholdMinutes", minutes.ToString());
            }
            var savedPauseWhenIdle = await db.GetSettingAsync("PauseTrackingWhenIdle", "false");
            var savedIdleThreshold = await db.GetSettingAsync("IdleThresholdMinutes", "10");
            tracker.ConfigureIdleTracking(
                savedPauseWhenIdle == "true",
                int.TryParse(savedIdleThreshold, out var savedIdleMinutes) ? savedIdleMinutes : 10);
            if (root.TryGetProperty("summaryEnabled", out var summaryEnabled))
                await db.SetSettingAsync("SummaryEnabled", summaryEnabled.GetBoolean() ? "true" : "false");
            if (root.TryGetProperty("summaryFrequency", out var summaryFrequency))
            {
                var frequency = Clean(summaryFrequency.GetString()).ToLowerInvariant();
                if (frequency is not ("daily" or "weekly" or "monthly"))
                    return Results.BadRequest(new { error = "Summary frequency must be daily, weekly, or monthly." });
                await db.SetSettingAsync("SummaryFrequency", frequency);
            }
            if (root.TryGetProperty("summaryTime", out var summaryTime))
            {
                var value = Clean(summaryTime.GetString());
                if (!TimeSpan.TryParse(value, out var parsedTime) || parsedTime < TimeSpan.Zero || parsedTime >= TimeSpan.FromDays(1))
                    return Results.BadRequest(new { error = "Invalid summary time." });
                await db.SetSettingAsync("SummaryTime", value);
            }
            if (root.TryGetProperty("summaryWeeklyDay", out var summaryWeeklyDay))
            {
                var value = summaryWeeklyDay.GetInt32();
                if (value is < 0 or > 6) return Results.BadRequest(new { error = "Invalid weekly summary day." });
                await db.SetSettingAsync("SummaryWeeklyDay", value.ToString());
            }
            if (root.TryGetProperty("summaryMonthlyDay", out var summaryMonthlyDay))
            {
                var value = summaryMonthlyDay.GetInt32();
                if (value is < 1 or > 31) return Results.BadRequest(new { error = "Invalid monthly summary day." });
                await db.SetSettingAsync("SummaryMonthlyDay", value.ToString());
            }
            if (root.TryGetProperty("tamperAlertsEnabled", out var tamperEnabled))
                await db.SetSettingAsync("TamperAlertsEnabled", tamperEnabled.GetBoolean() ? "true" : "false");
            if (root.TryGetProperty("hotKeyModifiers", out var hkm) || root.TryGetProperty("hotKeyKey", out var hkk))
            {
                var config = Program.GetConfig();
                var currentHotKey = await Program.GetDashboardHotKeySettings(config);
                var modifiers = root.TryGetProperty("hotKeyModifiers", out hkm)
                    ? Clean(hkm.GetString())
                    : currentHotKey.Modifiers;
                var key = root.TryGetProperty("hotKeyKey", out hkk)
                    ? Clean(hkk.GetString())
                    : currentHotKey.Key;

                if (!HotKeyService.TryParseHotKey(modifiers, key, out _, out _, out var normalizedModifiers, out var normalizedKey, out var hotKeyError))
                    return Results.BadRequest(new { error = hotKeyError });

                if (!await Program.UpdateDashboardHotKeyAsync(normalizedModifiers, normalizedKey))
                    return Results.BadRequest(new { error = "Could not register that hotkey. It may already be used by Windows or another app." });

                await db.SetSettingAsync("HotKeyModifiers", normalizedModifiers);
                await db.SetSettingAsync("HotKeyKey", normalizedKey);
            }
            if (root.TryGetProperty("autoStart", out var asv))
                Program.SetAutoStart(asv.GetBoolean());
            // Always reload email config after save
            await emailService.LoadSettingsAsync();
            if (emailService.IsEnabled) emailService.StartPolling(); else emailService.StopPolling();
            await Program.ReloadParentReportingAsync();
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

        app.MapGet("/api/settings/update-status", (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            var statusPath = Path.Combine(AppContext.BaseDirectory, "update-status.json");
            var logPath = Path.Combine(AppContext.BaseDirectory, "update.log");
            if (!File.Exists(statusPath))
                return Results.Ok(new
                {
                    status = "none",
                    message = "No update has been recorded yet.",
                    logPath,
                    logTail = ReadLogTail(logPath, 20)
                });

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statusPath));
                var root = doc.RootElement;
                return Results.Ok(new
                {
                    status = root.TryGetProperty("status", out var status) ? status.GetString() : "unknown",
                    source = root.TryGetProperty("source", out var source) ? source.GetString() : "",
                    startedAt = root.TryGetProperty("startedAt", out var startedAt) ? startedAt.GetString() : "",
                    finishedAt = root.TryGetProperty("finishedAt", out var finishedAt) ? finishedAt.GetString() : "",
                    message = root.TryGetProperty("message", out var message) ? message.GetString() : "",
                    logPath = root.TryGetProperty("logPath", out var savedLogPath) ? savedLogPath.GetString() : logPath,
                    logTail = ReadLogTail(logPath, 20)
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new
                {
                    status = "unknown",
                    message = "Could not read update status: " + ex.Message,
                    logPath,
                    logTail = ReadLogTail(logPath, 20)
                });
            }
        });

        app.MapPost("/api/settings/update", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            string source = "";
            string username = "";
            string password = "";
            string sha256 = "";
            if (ctx.Request.ContentLength > 0)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (doc.RootElement.TryGetProperty("source", out var src))
                    source = Clean(src.GetString());
                if (doc.RootElement.TryGetProperty("username", out var user))
                    username = Clean(user.GetString());
                if (doc.RootElement.TryGetProperty("password", out var pw))
                    password = pw.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("sha256", out var hash))
                    sha256 = Clean(hash.GetString()).ToUpperInvariant();
            }

            if (!IsValidUpdateSource(source, username, sha256, out var updateSourceError))
                return Results.BadRequest(new { error = updateSourceError });

            var updaterPath = Path.Combine(AppContext.BaseDirectory, "UpdateAgent.exe");
            if (!File.Exists(updaterPath))
                return Results.Json(new { error = "UpdateAgent.exe is missing from the app folder. Publish the app again with the updater included." }, statusCode: StatusCodes.Status500InternalServerError);

            var tempDir = Path.Combine(Path.GetTempPath(), "DeviceMonUpdateAgent", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var tempUpdaterPath = Path.Combine(tempDir, "UpdateAgent.exe");
            File.Copy(updaterPath, tempUpdaterPath, overwrite: true);

            var monitorPath = Program.CurrentExecutablePath;
            var targetDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var pid = Environment.ProcessId;
            var requestPath = Path.Combine(tempDir, "update-request.json");
            var statusPath = Path.Combine(targetDir, "update-status.json");
            WriteUpdateStatus(statusPath, new
            {
                status = "starting",
                source,
                startedAt = DateTimeOffset.Now,
                finishedAt = (DateTimeOffset?)null,
                message = "Starting updater silently.",
                logPath = Path.Combine(targetDir, "update.log")
            });

            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
            {
                source,
                targetDirectory = targetDir,
                monitorPath,
                monitorPid = pid,
                restart = true,
                username = string.IsNullOrWhiteSpace(username) ? null : username,
                protectedPassword = string.IsNullOrWhiteSpace(password) ? null : SecretProtector.Protect(password),
                sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256
            }, JsonOpts));

            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = tempUpdaterPath,
                    Arguments = $"--request \"{requestPath}\"",
                    WorkingDirectory = tempDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (process == null)
                    throw new InvalidOperationException("Windows did not start the updater process.");
            }
            catch (Exception ex)
            {
                WriteUpdateStatus(statusPath, new
                {
                    status = "failed",
                    source,
                    startedAt = DateTimeOffset.Now,
                    finishedAt = DateTimeOffset.Now,
                    message = "Could not start updater: " + ex.Message,
                    logPath = Path.Combine(targetDir, "update.log")
                });
                return Results.Json(new { error = "Could not start updater: " + ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }

            Logger.Instance.Warn($"Dashboard update started from source: {source}");
            Program.ShutdownSoon(1000);
            return Results.Ok(new { status = "update_started" });
        });

        app.MapGet("/api/config/export", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            var settings = await db.GetAllSettingsAsync();
            var backup = new ConfigBackup
            {
                ExportedAt = DateTime.Now,
                AppMappings = await db.GetAppMappingsAsync(),
                Limits = await db.GetLimitRulesAsync(),
                LimitGroups = await db.GetLimitGroupsAsync(),
                Schedules = await db.GetScheduleRulesAsync(),
                Settings = settings
                    .Where(kvp => ExportableSettings.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
            };

            return Results.Json(backup, JsonOpts);
        });

        app.MapPost("/api/config/import", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            ConfigBackup? backup;
            try
            {
                backup = await JsonSerializer.DeserializeAsync<ConfigBackup>(ctx.Request.Body, JsonOpts);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid backup JSON." });
            }

            if (backup == null)
                return Results.BadRequest(new { error = "Backup file is empty." });
            if (backup.Version != 1)
                return Results.BadRequest(new { error = "Unsupported backup version." });

            var validationError = ValidateBackup(backup);
            if (validationError != null)
                return Results.BadRequest(new { error = validationError });

            await db.ReplaceAppMappingsAsync(backup.AppMappings);
            await db.ReplaceLimitRulesAsync(backup.Limits);
            await db.ReplaceLimitGroupsAsync(backup.LimitGroups);
            await usageTracker.ReloadLimitGroupsAsync();
            await db.ReplaceScheduleRulesAsync(backup.Schedules);
            foreach (var setting in backup.Settings.Where(kvp => ExportableSettings.Contains(kvp.Key)))
                await db.SetSettingAsync(setting.Key, setting.Value);

            var importedConfig = Program.GetConfig();
            var importedHotKey = await Program.GetDashboardHotKeySettings(importedConfig);
            if (HotKeyService.TryParseHotKey(importedHotKey.Modifiers, importedHotKey.Key, out _, out _, out var normalizedModifiers, out var normalizedKey, out _) &&
                await Program.UpdateDashboardHotKeyAsync(normalizedModifiers, normalizedKey))
            {
                await db.SetSettingAsync("HotKeyModifiers", normalizedModifiers);
                await db.SetSettingAsync("HotKeyKey", normalizedKey);
            }

            enforcer.ClearExceeded();
            scheduler.InvalidateCache();
            tracker.LoadKnownApps(Program.GetConfig().KnownApps);
            foreach (var mapping in await db.GetAppMappingsAsync())
                tracker.AddKnownApp(mapping.ProcessName, mapping.AppName, mapping.CountInBackground, mapping.IgnoreOverlayFocus);
            var importedPauseWhenIdle = await db.GetSettingAsync("PauseTrackingWhenIdle", "false");
            var importedIdleThreshold = await db.GetSettingAsync("IdleThresholdMinutes", "10");
            tracker.ConfigureIdleTracking(
                importedPauseWhenIdle == "true",
                int.TryParse(importedIdleThreshold, out var importedIdleMinutes) ? importedIdleMinutes : 10);
            await emailService.LoadSettingsAsync();
            if (emailService.IsEnabled) emailService.StartPolling(); else emailService.StopPolling();
            await Program.ReloadParentReportingAsync();

            return Results.Ok(new { status = "imported" });
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
            var alerts = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            void SendAlert(string type, string appName, int? extra = null)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var data = extra.HasValue
                        ? $"{{\"type\":\"{type}\",\"appName\":\"{EscapeJson(appName)}\",\"value\":{extra}}}"
                        : $"{{\"type\":\"{type}\",\"appName\":\"{EscapeJson(appName)}\"}}";
                    alerts.Writer.TryWrite($"event: {type}\ndata: {data}\n\n");
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error($"SSE queue failed for {type}/{appName}: {ex.Message}");
                }
            }

            Action<string, int, string> breachHandler = (app, delay, proc) => SendAlert("breach", app, delay);
            Action<string, int> countdownHandler = (app, secs) => SendAlert("countdown", app, secs);
            Action<string> killedHandler = app => SendAlert("killed", app);
            Action<string> scheduleKillHandler = app => SendAlert("schedule_kill", app);

            enforcer.OnBreachAlert += breachHandler;
            enforcer.OnCountdownTick += countdownHandler;
            enforcer.OnAppKilled += killedHandler;
            enforcer.OnAppTerminatedBySchedule += scheduleKillHandler;

            try
            {
                await foreach (var alert in alerts.Reader.ReadAllAsync(ct))
                {
                    await ctx.Response.WriteAsync(alert, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                alerts.Writer.TryComplete();
                enforcer.OnBreachAlert -= breachHandler;
                enforcer.OnCountdownTick -= countdownHandler;
                enforcer.OnAppKilled -= killedHandler;
                enforcer.OnAppTerminatedBySchedule -= scheduleKillHandler;
            }
        });
    }

    private static async Task<string> GetOrCreateAdminTokenAsync(UsageDatabase db)
    {
        var token = CreateAdminToken();
        await PersistAdminTokenHashAsync(db, token);
        return token;
    }

    private static string CreateAdminToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private static async Task RotateAdminTokenAsync(UsageDatabase db)
    {
        _adminToken = CreateAdminToken();
        await PersistAdminTokenHashAsync(db, _adminToken);
    }

    private static async Task PersistAdminTokenHashAsync(UsageDatabase db, string token)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        await db.SetSettingAsync("DashboardAdminTokenHash", hash);
        await db.SetSettingAsync("DashboardAdminToken", "");
    }

    private static IResult? CheckLoginRateLimit(HttpContext ctx)
    {
        var key = GetAuthAttemptKey(ctx);
        if (!AuthAttempts.TryGetValue(key, out var state))
            return null;

        if (state.LockedUntil > DateTimeOffset.Now)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((state.LockedUntil - DateTimeOffset.Now).TotalSeconds));
            ctx.Response.Headers.RetryAfter = seconds.ToString();
            return Results.Json(
                new { error = $"Too many failed login attempts. Try again in {seconds} seconds." },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (state.LockedUntil != default)
            AuthAttempts.TryRemove(key, out _);

        return null;
    }

    private static void RegisterFailedLogin(HttpContext ctx)
    {
        var key = GetAuthAttemptKey(ctx);
        var lockoutTriggered = false;
        AuthAttempts.AddOrUpdate(
            key,
            _ => new AuthAttemptState { FailedCount = 1 },
            (_, state) =>
            {
                state.FailedCount++;
                if (state.FailedCount >= MaxFailedLoginAttempts)
                {
                    lockoutTriggered = state.LockedUntil <= DateTimeOffset.Now;
                    state.LockedUntil = DateTimeOffset.Now.Add(AuthLockoutDuration);
                    Logger.Instance.Warn($"Dashboard login temporarily locked for {key}");
                }
                return state;
            });
        if (lockoutTriggered)
            LoginLockoutDetected?.Invoke(key);
    }

    private static void ResetLoginRateLimit(HttpContext ctx)
    {
        AuthAttempts.TryRemove(GetAuthAttemptKey(ctx), out _);
    }

    private static string GetAuthAttemptKey(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "local";

    private static IResult? RequireWriteAccess(HttpContext ctx)
    {
        if (!_adminPasswordSet)
        {
            if (IsLocalSetupAllowed(ctx) && HttpMethods.IsGet(ctx.Request.Method))
                return null;

            return Results.Json(
                new { error = "Set an admin password locally before using dashboard actions." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var provided = GetProvidedAdminToken(ctx);
        if (IsValidAdminToken(provided))
            return null;

        return Results.Json(new { error = "Admin token required." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    private static bool ShouldGateDashboardAsset(HttpContext ctx)
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
            return false;

        var path = ctx.Request.Path.Value ?? "";
        if (path.Equals("/login.html", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!_adminPasswordSet)
            return !IsLocalSetupAllowed(ctx);

        return !IsValidAdminToken(GetProvidedAdminToken(ctx));
    }

    private static bool IsLocalSetupAllowed(HttpContext ctx) =>
        IsLocalRequest(ctx) && !HasExternalForwardingHeaders(ctx);

    private static string? GetProvidedAdminToken(HttpContext ctx)
    {
        var provided = ctx.Request.Headers["X-Admin-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(provided))
            provided = ctx.Request.Cookies[AdminCookieName];
        return provided;
    }

    private static bool IsValidAdminToken(string? provided)
    {
        var providedBytes = System.Text.Encoding.UTF8.GetBytes(provided ?? "");
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(_adminToken);
        return !string.IsNullOrWhiteSpace(_adminToken) &&
            providedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static void SetAdminCookie(HttpContext ctx)
    {
        ctx.Response.Cookies.Append(AdminCookieName, _adminToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = IsExternalHttps(ctx),
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
    }

    private static void ClearAdminCookie(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(AdminCookieName, new CookieOptions
        {
            Secure = IsExternalHttps(ctx),
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
    }

    private static bool IsExternalHttps(HttpContext ctx) =>
        ctx.Request.IsHttps ||
        string.Equals(ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault(), "https", StringComparison.OrdinalIgnoreCase) ||
        (ctx.Request.Headers["Cf-Visitor"].FirstOrDefault()?.Contains("\"scheme\":\"https\"", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool HasExternalForwardingHeaders(HttpContext ctx) =>
        ctx.Request.Headers.ContainsKey("CF-Connecting-IP") ||
        ctx.Request.Headers.ContainsKey("CF-Ray") ||
        ctx.Request.Headers.ContainsKey("X-Forwarded-For") ||
        ctx.Request.Headers.ContainsKey("X-Real-IP") ||
        ctx.Request.Headers.ContainsKey("Forwarded");

    private static bool IsLocalRequest(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip == null || System.Net.IPAddress.IsLoopback(ip);
    }

    private static string Clean(string? value) => InputValidation.Clean(value);

    private static bool IsValidAppName(string value) => InputValidation.IsValidAppName(value);

    private static bool IsValidProcessName(string value) => InputValidation.IsValidProcessName(value);

    private static bool IsHttpUrl(string value) => InputValidation.IsValidHttpUrl(value);

    private static bool IsValidUpdateSource(string source, string username, string sha256, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(source) || source.Length > 2048 || source.Any(char.IsControl))
        {
            error = "Update source is required.";
            return false;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                error = "Remote update URLs must use HTTPS.";
                return false;
            }
            if (sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c)))
            {
                error = "HTTPS updates require a valid 64-character SHA-256.";
                return false;
            }
            return true;
        }

        if (source.StartsWith(@"\\", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(username))
            return true;

        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(source));
            if (!Directory.Exists(fullPath))
            {
                error = "Update source folder does not exist on the child PC.";
                return false;
            }

            return true;
        }
        catch
        {
            error = "Update source must be a folder path, UNC path, or http/https ZIP URL.";
            return false;
        }
    }

    private static bool IsValidEmail(string value) =>
        MimeKit.MailboxAddress.TryParse(value, out _);

    private static bool IsValidEmailList(string value)
    {
        var emails = value.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return emails.Length > 0 && emails.All(IsValidEmail);
    }

    private static readonly HashSet<string> ExportableSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "KillDelaySeconds",
        "ShowWarning",
        "WarningMessage",
        "WebhookUrl",
        "EmailAddress",
        "EmailAllowedSender",
        "EmailNotifyEnabled",
        "EmailBreachNotifyEnabled",
        "EmailKillNotifyEnabled",
        "EmailStartNotifyEnabled",
        "EmailControlEnabled",
        "EmailDeviceId",
        "UiLanguage",
        "PauseTrackingWhenIdle",
        "IdleThresholdMinutes",
        "SummaryEnabled",
        "SummaryFrequency",
        "SummaryTime",
        "SummaryWeeklyDay",
        "SummaryMonthlyDay",
        "TamperAlertsEnabled",
        "HotKeyModifiers",
        "HotKeyKey"
    };

    private static readonly HashSet<string> AllowedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "de", "es", "ru", "fr"
    };

    private static string? ValidateBackup(ConfigBackup backup)
    {
        if (backup.AppMappings.Count > 500 || backup.Limits.Count > 500 || backup.Schedules.Count > 500 || backup.LimitGroups.Count > 100)
            return "Backup contains too many records.";

        var cleanMappings = new List<AppMapping>();
        foreach (var mapping in backup.AppMappings)
        {
            var processName = Clean(mapping.ProcessName);
            var appName = Clean(mapping.AppName);
            if (!IsValidProcessName(processName) || !IsValidAppName(appName))
                return "Backup contains an invalid app mapping.";
            cleanMappings.Add(new AppMapping(
                processName,
                appName,
                mapping.CountInBackground,
                mapping.IgnoreOverlayFocus));
        }
        backup.AppMappings = cleanMappings;

        foreach (var limit in backup.Limits)
        {
            limit.AppName = Clean(limit.AppName);
            if (!IsValidAppName(limit.AppName) || limit.DailyMaxMinutes is < 1 or > 1440)
                return "Backup contains an invalid app limit.";
        }

        var groupedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in backup.LimitGroups)
        {
            group.Id = 0;
            group.Name = Clean(group.Name);
            group.AppNames = group.AppNames.Select(Clean).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!IsValidAppName(group.Name) || group.DailyMaxMinutes is < 1 or > 1440 || group.AppNames.Count == 0 ||
                group.AppNames.Count > 100 || group.AppNames.Any(name => !IsValidAppName(name)))
                return "Backup contains an invalid limit group.";
            if (group.AppNames.Any(name => !groupedApps.Add(name)))
                return "An app belongs to more than one limit group in the backup.";
        }

        foreach (var rule in backup.Schedules)
        {
            if (!NormalizeScheduleRule(rule))
                return "Backup contains an invalid schedule rule.";
        }

        var cleanSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in backup.Settings)
        {
            if (!ExportableSettings.Contains(key))
                continue;

            var cleanValue = Clean(value);
            switch (key)
            {
                case "KillDelaySeconds":
                    if (!int.TryParse(cleanValue, out var delay) || delay is < 5 or > 300)
                        return "Backup contains an invalid kill delay.";
                    break;
                case "ShowWarning":
                case "PauseTrackingWhenIdle":
                case "SummaryEnabled":
                case "TamperAlertsEnabled":
                case "EmailNotifyEnabled":
                case "EmailBreachNotifyEnabled":
                case "EmailKillNotifyEnabled":
                case "EmailStartNotifyEnabled":
                case "EmailControlEnabled":
                    if (!bool.TryParse(cleanValue, out _))
                        return $"Backup contains an invalid boolean setting: {key}.";
                    break;
                case "IdleThresholdMinutes":
                    if (!int.TryParse(cleanValue, out var idleMinutes) || idleMinutes is < 1 or > 240)
                        return "Backup contains an invalid idle threshold.";
                    break;
                case "SummaryFrequency":
                    if (cleanValue is not ("daily" or "weekly" or "monthly"))
                        return "Backup contains an invalid summary frequency.";
                    break;
                case "SummaryTime":
                    if (!TimeSpan.TryParse(cleanValue, out var summaryTime) || summaryTime < TimeSpan.Zero || summaryTime >= TimeSpan.FromDays(1))
                        return "Backup contains an invalid summary time.";
                    break;
                case "SummaryWeeklyDay":
                    if (!int.TryParse(cleanValue, out var weeklyDay) || weeklyDay is < 0 or > 6)
                        return "Backup contains an invalid weekly summary day.";
                    break;
                case "SummaryMonthlyDay":
                    if (!int.TryParse(cleanValue, out var monthlyDay) || monthlyDay is < 1 or > 31)
                        return "Backup contains an invalid monthly summary day.";
                    break;
                case "EmailDeviceId":
                    var normalizedDeviceId = EmailService.NormalizeDeviceId(cleanValue);
                    if (normalizedDeviceId.Length is < 1 or > 40)
                        return "Backup contains an invalid email device ID.";
                    cleanValue = normalizedDeviceId;
                    break;
                case "UiLanguage":
                    if (!AllowedLanguages.Contains(cleanValue))
                        return "Backup contains an unsupported language.";
                    break;
                case "HotKeyModifiers":
                    if (!HotKeyService.TryParseHotKey(cleanValue, backup.Settings.GetValueOrDefault("HotKeyKey", "H"), out _, out _, out _, out _, out _))
                        return "Backup contains an invalid hotkey modifier.";
                    break;
                case "HotKeyKey":
                    if (!HotKeyService.TryParseHotKey(backup.Settings.GetValueOrDefault("HotKeyModifiers", "Control+Alt"), cleanValue, out _, out _, out _, out _, out _))
                        return "Backup contains an invalid hotkey key.";
                    break;
                case "WebhookUrl":
                    if (!string.IsNullOrEmpty(cleanValue) && !IsHttpUrl(cleanValue))
                        return "Backup contains an invalid webhook URL.";
                    break;
                case "WarningMessage":
                    if (cleanValue.Length > 200)
                        return "Backup contains a warning message that is too long.";
                    break;
                case "EmailAddress":
                    if (!string.IsNullOrEmpty(cleanValue) && !IsValidEmail(cleanValue))
                        return "Backup contains an invalid email address.";
                    break;
                case "EmailAllowedSender":
                    if (!string.IsNullOrEmpty(cleanValue) && !IsValidEmailList(cleanValue))
                        return "Backup contains an invalid allowed sender list.";
                    break;
            }

            cleanSettings[key] = cleanValue;
        }

        backup.Settings = cleanSettings;
        return null;
    }

    private static bool NormalizeScheduleRule(ScheduleRule rule)
    {
        rule.AppName = Clean(rule.AppName);
        rule.DayOfWeek = Clean(rule.DayOfWeek);
        rule.StartTime = Clean(rule.StartTime);
        rule.EndTime = Clean(rule.EndTime);

        if (!string.IsNullOrEmpty(rule.AppName) && !IsValidAppName(rule.AppName))
            return false;
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

    private static void WriteUpdateStatus(string path, object status)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(status, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to write update status: {ex.Message}");
        }
    }

    private static string[] ReadLogTail(string path, int count)
    {
        try
        {
            if (!File.Exists(path))
                return [];

            return File.ReadLines(path)
                .TakeLast(Math.Max(1, count))
                .ToArray();
        }
        catch (Exception ex)
        {
            return [$"Could not read update log: {ex.Message}"];
        }
    }

    public static async Task StopAsync()
    {
        if (_app != null)
            await _app.StopAsync();
    }

    private sealed class AuthAttemptState
    {
        public int FailedCount { get; set; }
        public DateTimeOffset LockedUntil { get; set; }
    }
}
