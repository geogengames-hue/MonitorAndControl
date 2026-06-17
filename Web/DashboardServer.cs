using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Diagnostics;
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
                !ctx.Request.Path.StartsWithSegments("/api/health") &&
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
                if (!IsLocalRequest(ctx))
                    return Results.Json(new { error = "Admin password has not been set locally yet." }, statusCode: StatusCodes.Status403Forbidden);
                return Results.Ok(new { token = _adminToken, passwordSet = false });
            }

            if (!PasswordHasher.Verify(password, storedHash))
            {
                RegisterFailedLogin(ctx);
                return Results.Json(new { error = "Invalid admin password." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            ResetLoginRateLimit(ctx);
            return Results.Ok(new { token = _adminToken, passwordSet = true });
        });

        app.MapGet("/api/auth/status", () =>
            Results.Ok(new { passwordSet = _adminPasswordSet }));

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
                if (auth != null && !PasswordHasher.Verify(currentPassword, storedHash))
                    return auth;
            }

            await db.SetSettingAsync("DashboardAdminPasswordHash", PasswordHasher.Hash(newPassword));
            _adminPasswordSet = true;
            return Results.Ok(new { status = "password_set", token = _adminToken });
        });

        app.MapPost("/api/auth/token/rotate", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            _adminToken = CreateAdminToken();
            await db.SetSettingAsync("DashboardAdminToken", _adminToken);
            Logger.Instance.Warn("Dashboard admin token rotated");
            return Results.Ok(new { status = "rotated", token = _adminToken });
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
            await db.ClearTodayUsageAsync();
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
            var countdownApps = new List<object>();
            return Results.Json(new
            {
                currentApp = tracker.CurrentAppName ?? "None",
                currentProcess = tracker.CurrentProcessName ?? "None",
                isTracking = tracker.CurrentAppName != null,
                enforcementPaused = enforcer.IsPaused,
                pausedUntil = enforcer.PausedUntil
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
            await db.DeleteAppUsageAsync(appName);

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
                        catch (Exception ex)
                        {
                            Logger.Instance.Error($"Failed to inspect process {p.Id}: {ex.Message}");
                            return false;
                        }
                    })
                    .Select(p =>
                    {
                        try { return new { name = p.ProcessName + ".exe", title = p.MainWindowTitle ?? "", pid = p.Id }; }
                        catch (Exception ex)
                        {
                            Logger.Instance.Error($"Failed to read process details for {p.Id}: {ex.Message}");
                            return null;
                        }
                    })
                    .Where(x => x != null)
                    .DistinctBy(x => x!.name)
                    .ToList();
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
            var emailStartNotifyEnabled = await db.GetSettingAsync("EmailStartNotifyEnabled", "false");
            var emailControlEnabled = await db.GetSettingAsync("EmailControlEnabled", "false");
            var emailDeviceId = await db.GetSettingAsync("EmailDeviceId", Environment.MachineName);
            var uiLanguage = await db.GetSettingAsync("UiLanguage", "en");
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
                emailStartNotifyEnabled = emailStartNotifyEnabled == "true",
                emailControlEnabled = emailControlEnabled == "true",
                emailDeviceId = EmailService.NormalizeDeviceId(emailDeviceId),
                uiLanguage,
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
                await db.SetSettingAsync("EmailNotifyEnabled", en.GetBoolean() ? "true" : "false");
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

        app.MapPost("/api/settings/update", async (HttpContext ctx) =>
        {
            var auth = RequireWriteAccess(ctx);
            if (auth != null) return auth;

            string source = "";
            string username = "";
            string password = "";
            if (ctx.Request.ContentLength > 0)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (doc.RootElement.TryGetProperty("source", out var src))
                    source = Clean(src.GetString());
                if (doc.RootElement.TryGetProperty("username", out var user))
                    username = Clean(user.GetString());
                if (doc.RootElement.TryGetProperty("password", out var pw))
                    password = pw.GetString() ?? "";
            }

            if (!IsValidUpdateSource(source, username, out var updateSourceError))
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
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
            {
                source,
                targetDirectory = targetDir,
                monitorPath,
                monitorPid = pid,
                restart = true,
                username = string.IsNullOrWhiteSpace(username) ? null : username,
                password = string.IsNullOrWhiteSpace(password) ? null : password
            }, JsonOpts));

            Process.Start(new ProcessStartInfo
            {
                FileName = tempUpdaterPath,
                Arguments = $"--request \"{requestPath}\"",
                WorkingDirectory = tempDir,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

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
                tracker.AddKnownApp(mapping.ProcessName, mapping.AppName);
            await emailService.LoadSettingsAsync();
            if (emailService.IsEnabled) emailService.StartPolling(); else emailService.StopPolling();

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
                catch (Exception ex)
                {
                    Logger.Instance.Error($"SSE send failed for {type}/{appName}: {ex.Message}");
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
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) { }
            finally
            {
                enforcer.OnBreachAlert -= breachHandler;
                enforcer.OnCountdownTick -= countdownHandler;
                enforcer.OnAppKilled -= killedHandler;
                enforcer.OnAppTerminatedBySchedule -= scheduleKillHandler;
            }
        });
    }

    private static async Task<string> GetOrCreateAdminTokenAsync(UsageDatabase db)
    {
        var token = await db.GetSettingAsync("DashboardAdminToken", "");
        if (!string.IsNullOrWhiteSpace(token))
            return token;

        token = CreateAdminToken();
        await db.SetSettingAsync("DashboardAdminToken", token);
        return token;
    }

    private static string CreateAdminToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

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
        AuthAttempts.AddOrUpdate(
            key,
            _ => new AuthAttemptState { FailedCount = 1 },
            (_, state) =>
            {
                state.FailedCount++;
                if (state.FailedCount >= MaxFailedLoginAttempts)
                {
                    state.LockedUntil = DateTimeOffset.Now.Add(AuthLockoutDuration);
                    Logger.Instance.Warn($"Dashboard login temporarily locked for {key}");
                }
                return state;
            });
    }

    private static void ResetLoginRateLimit(HttpContext ctx)
    {
        AuthAttempts.TryRemove(GetAuthAttemptKey(ctx), out _);
    }

    private static string GetAuthAttemptKey(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "local";

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

    private static bool IsValidUpdateSource(string source, string username, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(source) || source.Length > 2048 || source.Any(char.IsControl))
        {
            error = "Update source is required.";
            return false;
        }

        if (IsHttpUrl(source))
            return true;

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
        "EmailStartNotifyEnabled",
        "EmailControlEnabled",
        "EmailDeviceId",
        "UiLanguage",
        "HotKeyModifiers",
        "HotKeyKey"
    };

    private static readonly HashSet<string> AllowedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "de", "es", "ru", "fr"
    };

    private static string? ValidateBackup(ConfigBackup backup)
    {
        if (backup.AppMappings.Count > 500 || backup.Limits.Count > 500 || backup.Schedules.Count > 500)
            return "Backup contains too many records.";

        var cleanMappings = new List<AppMapping>();
        foreach (var mapping in backup.AppMappings)
        {
            var processName = Clean(mapping.ProcessName);
            var appName = Clean(mapping.AppName);
            if (!IsValidProcessName(processName) || !IsValidAppName(appName))
                return "Backup contains an invalid app mapping.";
            cleanMappings.Add(new AppMapping(processName, appName));
        }
        backup.AppMappings = cleanMappings;

        foreach (var limit in backup.Limits)
        {
            limit.AppName = Clean(limit.AppName);
            if (!IsValidAppName(limit.AppName) || limit.DailyMaxMinutes is < 1 or > 1440)
                return "Backup contains an invalid app limit.";
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
                case "EmailNotifyEnabled":
                case "EmailStartNotifyEnabled":
                case "EmailControlEnabled":
                    if (!bool.TryParse(cleanValue, out _))
                        return $"Backup contains an invalid boolean setting: {key}.";
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
