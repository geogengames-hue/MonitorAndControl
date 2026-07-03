using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using MonitorAndControl.Data;
using MonitorAndControl.Models;
using MonitorAndControl.Services;
using MonitorAndControl.UI;
using MonitorAndControl.Web;

namespace MonitorAndControl;

internal static class Program
{
    private const string AppName = "DeviceMon";
    private const string LegacyAppName = "SystemHelper";
    private const string WatchdogServiceName = "GameHost";
    private const string LegacyWatchdogServiceName = "MonitorAndControlWatchdog";
    private const string WatchdogExeName = "GameHost.exe";
    private const string LegacyWatchdogExeName = "MonitorAndControlWatchdog.exe";
    private const string OlderWatchdogExeName = "SystemHelperWatchdog.exe";

    private static UsageDatabase? _db;
    private static WindowTracker? _tracker;
    private static UsageTracker? _usageTracker;
    private static LimitEnforcer? _enforcer;
    private static SchedulerService? _scheduler;
    private static NotificationService? _notifier;
    private static EmailService? _emailService;
    private static ParentReportService? _parentReportService;
    private static HiddenForm? _hiddenForm;
    private static CancellationTokenSource? _cts;

    private static Mutex? _instanceMutex;
    private static bool _isAutoStartLaunch;

    [STAThread]
    static void Main(string[] args)
    {
        // Single-instance check
        _instanceMutex = new Mutex(true, "DeviceMon_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Logger.Instance.Warn("Another instance already running - exiting");
            return;
        }

        _isAutoStartLaunch = IsAutoStartLaunch(args);

        ApplicationConfiguration.Initialize();
        _cts = new CancellationTokenSource();

        Logger.Instance.Info($"Monitor started on {Environment.MachineName}");
        try
        {
            var config = LoadConfig();
            Initialize(config).GetAwaiter().GetResult();
            RegisterAutoStart();
            EnsureWatchdogInstalled();
            StartServices(config);

            var (hotKeyModifiers, hotKeyKey) = GetDashboardHotKeySettings(config).GetAwaiter().GetResult();
            var (mods, key) = HotKeyService.ParseHotKey(hotKeyModifiers, hotKeyKey);

            _hiddenForm = new HiddenForm(1, mods, key, () =>
            {
                HotKeyService.OpenDashboard($"http://localhost:{DashboardServer.Port}");
            });

            _hiddenForm.FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    _hiddenForm.Hide();
                }
            };

            Application.Run(_hiddenForm);
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "SystemHelper_error.log"),
                $"{DateTime.Now}: {ex}");
        }
        finally
        {
            Cleanup().GetAwaiter().GetResult();
        }
    }

    private static AppConfig? _cachedConfig;

    public static AppConfig GetConfig() => _cachedConfig ?? new AppConfig();

    private static AppConfig LoadConfig()
    {
        var path = GetConfigPath();

        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            _cachedConfig = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            return _cachedConfig;
        }
        _cachedConfig = new AppConfig();
        return _cachedConfig;
    }

    private static string GetConfigPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        return File.Exists(path) ? path : Path.GetFullPath("appsettings.json");
    }

    private static void RegisterAutoStart()
    {
        SetAutoStart(true);
    }

    public static bool GetAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue(AppName) != null || key?.GetValue(LegacyAppName) != null;
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to read autostart setting: {ex.Message}");
            return false;
        }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable)
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                {
                    key.SetValue(AppName, $"\"{exePath}\" --autostart");
                    key.DeleteValue(LegacyAppName, false);
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
                key.DeleteValue(LegacyAppName, false);
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to update autostart setting: {ex.Message}");
        }
    }

    private static void EnsureWatchdogInstalled()
    {
        try
        {
            var watchdogPath = GetWatchdogPath();
            if (string.IsNullOrEmpty(watchdogPath)) return;
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null) return;

            using var sc = new System.ServiceProcess.ServiceController(WatchdogServiceName);
            _ = sc.Status;
            // Service exists - check if binPath matches current location.
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{WatchdogServiceName}");
                var currentBinPath = key?.GetValue("ImagePath") as string ?? "";
                if (ServicePointsToWatchdog(currentBinPath, watchdogPath))
                {
                    StartWatchdogServiceIfNeeded(sc, watchdogPath, exePath);
                    return; // Path matches, nothing to do
                }

                Logger.Instance.Info("Watchdog path changed, updating service...");
                if (SuppressWatchdogElevation())
                {
                    Logger.Instance.Warn("Skipping elevated watchdog service path update because elevation is disabled for this run.");
                    StartWatchdogServiceIfNeeded(sc, watchdogPath, exePath);
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = watchdogPath,
                    Arguments = $"--update --monitor \"{exePath}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(30000);
                Logger.Instance.Info("Watchdog service path updated");
                StartWatchdogServiceIfNeeded(sc, watchdogPath, exePath);
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"Failed to inspect watchdog service path: {ex.Message}");
                return;
            }
            return;
        }
        catch (InvalidOperationException) { }

        try
        {
            var watchdogPath = GetWatchdogPath();
            if (string.IsNullOrEmpty(watchdogPath)) return;
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null) return;

            Logger.Instance.Info("Installing watchdog service...");
            if (SuppressWatchdogElevation())
            {
                Logger.Instance.Warn("Skipping elevated watchdog service installation because elevation is disabled for this run.");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = watchdogPath,
                Arguments = $"--install --monitor \"{exePath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(30000);
            Logger.Instance.Info("Watchdog service installation completed");
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to install watchdog: {ex.Message}");
        }
    }

    private static bool IsAutoStartLaunch(string[] args)
    {
        if (args.Contains("--autostart", StringComparer.OrdinalIgnoreCase))
            return true;

        if (string.Equals(Environment.GetEnvironmentVariable("DEVICEMON_SUPPRESS_WATCHDOG_UAC"), "1", StringComparison.Ordinal))
            return true;

        // Covers existing Run-key entries that have not yet picked up --autostart.
        if (GetAutoStart() && Environment.TickCount64 < 120_000)
            return true;

        return false;
    }

    private static bool SuppressWatchdogElevation() => _isAutoStartLaunch;

    private static string ExtractServiceExecutable(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return "";

        var trimmed = imagePath.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote > 1)
                return trimmed[1..closingQuote];
        }

        var spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex >= 0 ? trimmed[..spaceIndex].Trim('"') : trimmed.Trim('"');
    }

    private static bool ServicePointsToWatchdog(string imagePath, string watchdogPath)
    {
        var serviceExe = ExtractServiceExecutable(imagePath);
        if (string.IsNullOrEmpty(serviceExe))
            return false;

        try
        {
            return Path.GetFullPath(serviceExe).Equals(Path.GetFullPath(watchdogPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return serviceExe.Equals(watchdogPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void StartWatchdogServiceIfNeeded(System.ServiceProcess.ServiceController sc, string watchdogPath, string monitorPath)
    {
        try
        {
            sc.Refresh();
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running ||
                sc.Status == System.ServiceProcess.ServiceControllerStatus.StartPending)
                return;

            Logger.Instance.Warn($"Watchdog service is {sc.Status}; starting it...");
            sc.Start();
            sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            Logger.Instance.Info("Watchdog service started");
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to start watchdog service: {ex.Message}");
            TryElevatedWatchdogUpdate(watchdogPath, monitorPath);
        }
    }

    private static void TryElevatedWatchdogUpdate(string watchdogPath, string monitorPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(watchdogPath) || !File.Exists(watchdogPath))
                return;

            if (SuppressWatchdogElevation())
            {
                Logger.Instance.Warn("Skipping elevated watchdog service repair/start during autostart. Start DeviceMon manually to repair GameHost.");
                return;
            }

            Logger.Instance.Warn("Requesting elevated watchdog service repair/start...");
            var psi = new ProcessStartInfo
            {
                FileName = watchdogPath,
                Arguments = $"--update --monitor \"{monitorPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(30000);
        }
        catch (Exception repairEx)
        {
            Logger.Instance.Error($"Elevated watchdog repair/start failed: {repairEx.Message}");
        }
    }

    private static string GetWatchdogPath()
    {
        var dir = AppContext.BaseDirectory;
        var path = Path.Combine(dir, WatchdogExeName);
        if (File.Exists(path)) return path;
        path = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? dir, WatchdogExeName);
        if (File.Exists(path)) return path;
        path = Path.Combine(dir, LegacyWatchdogExeName);
        if (File.Exists(path)) return path;
        path = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? dir, LegacyWatchdogExeName);
        if (File.Exists(path)) return path;
        path = Path.Combine(dir, OlderWatchdogExeName);
        if (File.Exists(path)) return path;
        path = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? dir, OlderWatchdogExeName);
        return File.Exists(path) ? path : "";
    }

    public static void Shutdown()
    {
        Cleanup().GetAwaiter().GetResult();
        try { Environment.Exit(0); } catch (Exception ex) { Logger.Instance.Error($"Failed to exit process: {ex.Message}"); }
    }

    public static void ShutdownSoon(int delayMs = 500)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(Math.Clamp(delayMs, 100, 10000));
            Shutdown();
        });
    }

    public static string CurrentExecutablePath =>
        System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "DeviceMon.exe");

    private static async Task Initialize(AppConfig config)
    {
        _db = new UsageDatabase();
        await _db.InitializeDefaults(config);

        if (string.IsNullOrEmpty(await _db.GetSettingAsync("KillDelaySeconds", "")))
            await _db.SetKillDelayAsync(config.KillDelaySeconds);
        if (string.IsNullOrEmpty(await _db.GetSettingAsync("ShowWarning", "")))
            await _db.SetShowWarningAsync(config.ShowWarningOnChildPc);
        if (string.IsNullOrEmpty(await _db.GetSettingAsync("HotKeyModifiers", "")))
            await _db.SetSettingAsync("HotKeyModifiers", config.HotKeyModifiers);
        if (string.IsNullOrEmpty(await _db.GetSettingAsync("HotKeyKey", "")))
            await _db.SetSettingAsync("HotKeyKey", config.HotKeyKey);
    }

    public static async Task<(string Modifiers, string Key)> GetDashboardHotKeySettings(AppConfig config)
    {
        if (_db == null)
            return (config.HotKeyModifiers, config.HotKeyKey);

        var modifiers = await _db.GetSettingAsync("HotKeyModifiers", config.HotKeyModifiers);
        var key = await _db.GetSettingAsync("HotKeyKey", config.HotKeyKey);
        return (modifiers, key);
    }

    public static async Task<bool> UpdateDashboardHotKeyAsync(string modifiers, string key)
    {
        if (!HotKeyService.TryParseHotKey(modifiers, key, out var mods, out var vk, out _, out _, out _))
            return false;

        if (_hiddenForm == null)
            return true;

        return await _hiddenForm.UpdateHotKeyAsync(mods, vk);
    }

    public static async Task ReloadParentReportingAsync()
    {
        if (_parentReportService != null)
            await _parentReportService.ReloadAndCheckAsync();
    }

    private static void StartServices(AppConfig config)
    {
        _tracker = new WindowTracker();
        _tracker.LoadKnownApps(config.KnownApps);
        // Load dynamic app mappings from database
        var mappings = _db!.GetAppMappingsAsync().GetAwaiter().GetResult();
        foreach (var m in mappings)
            _tracker.AddKnownApp(m.ProcessName, m.AppName, m.CountInBackground, m.IgnoreOverlayFocus);
        var pauseWhenIdleText = _db.GetSettingAsync("PauseTrackingWhenIdle", "false").GetAwaiter().GetResult();
        var idleThresholdText = _db.GetSettingAsync("IdleThresholdMinutes", "10").GetAwaiter().GetResult();
        _tracker.ConfigureIdleTracking(
            bool.TryParse(pauseWhenIdleText, out var pauseWhenIdle) && pauseWhenIdle,
            int.TryParse(idleThresholdText, out var idleThresholdMinutes) ? idleThresholdMinutes : 10);
        _tracker.Start(config.PollIntervalMs);

        // Copy PopupHost to hidden appdata location
        GetPopupHostPath();

        _scheduler = new SchedulerService(_db!);
        _scheduler.InvalidateCache();

        _enforcer = new LimitEnforcer(_db!, _tracker);
        _enforcer.OnBreachAlert += ShowLimitWarningPopup;
        _enforcer.OnBreachAlert += (app, delay, proc) => Logger.Instance.Info($"Limit breach: {app} - closing in {delay}s");
        _enforcer.OnAppKilled += OnAppKilled;
        _enforcer.OnAppKilled += (app) => Logger.Instance.Warn($"App killed: {app}");
        _enforcer.OnAppTerminatedBySchedule += ShowScheduleWarningPopup;
        _enforcer.OnAppTerminatedBySchedule += (app) => Logger.Instance.Warn($"Schedule kill: {app}");

        _notifier = new NotificationService(_db!);
        _enforcer.OnBreachAlert += (app, delay, proc) => _ = _notifier.NotifyBreachAsync(app, delay);
        _enforcer.OnAppKilled += (app) => _ = _notifier.NotifyKilledAsync(app);
        _enforcer.OnAppTerminatedBySchedule += (app) => _ = _notifier.NotifyScheduleTerminatedAsync(app);

        _emailService = new EmailService(_db!, _tracker!, _enforcer!, _scheduler!);
        _emailService.LoadSettingsAsync().GetAwaiter().GetResult();
        _emailService.StartPolling();
        _parentReportService = new ParentReportService(_db!, _emailService, _notifier!, GetConfigPath());
        DashboardServer.LoginLockoutDetected = _parentReportService.ReportLoginLockout;
        _parentReportService.Start();
        _enforcer.OnBreachAlert += (app, delay, proc) => _ = _emailService.SendBreachAlertAsync($"Limit Breach: {app}", $"{app} exceeded its daily limit. Closing in {delay}s.");
        _enforcer.OnAppKilled += (app) => _ = _emailService.SendKillAlertAsync($"App Closed: {app}", $"{app} was closed after exceeding its limit.");
        _enforcer.OnAppTerminatedBySchedule += (app) => _ = _emailService.SendKillAlertAsync($"Schedule Block: {app}", $"{app} was closed by schedule rule.");
        _tracker.OnActiveWindowChanged += (app, proc) => _ = _emailService.NotifyTrackedAppStartedAsync(app, proc);
        _ = SendWatchdogRestartAlertAsync();
        _ = _enforcer.RehydrateExceededTodayAsync();

        _usageTracker = new UsageTracker(_db!, _tracker, _enforcer, _scheduler);
        _usageTracker.Start(config.FlushIntervalSec);

        var webTask = Task.Run(() => StartWebServer(config));
    }

    private static async Task StartWebServer(AppConfig config)
    {
        try
        {
            await DashboardServer.StartAsync(
                _db!, _tracker!, _enforcer!, _scheduler!, _usageTracker!, _emailService!, config);
        }
        catch (Exception ex)
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "SystemHelper_error.log"),
                $"{DateTime.Now}: Web server error: {ex}");
        }
    }

    private static async Task SendWatchdogRestartAlertAsync()
    {
        try
        {
            if (_emailService == null || _parentReportService == null) return;
            var marker = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SystemHelper",
                "watchdog-restart.marker");
            if (!File.Exists(marker)) return;

            _emailService.SuppressStartAlertsFor(TimeSpan.FromMinutes(2));
            var markerText = await File.ReadAllTextAsync(marker);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if (await _parentReportService.ReportWatchdogRestartAsync(markerText))
                {
                    File.Delete(marker);
                    return;
                }
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(30));
            }
            Logger.Instance.Error("Watchdog restart alert could not be delivered after three attempts; marker retained for the next startup.");
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to send watchdog restart alert: {ex.Message}");
        }
    }

    private static string GetPopupHostPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemHelper", "popup");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "PopupHost.exe");
        var srcDir = AppContext.BaseDirectory;
        var src = Path.Combine(srcDir, "PopupHost.exe");
        if (!File.Exists(src))
            src = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? srcDir, "PopupHost.exe");
        if (File.Exists(src))
        {
            var srcTime = File.GetLastWriteTimeUtc(src);
            var destTime = File.Exists(dest) ? File.GetLastWriteTimeUtc(dest) : DateTime.MinValue;
            if (srcTime > destTime)
                File.Copy(src, dest, overwrite: true);
        }
        return File.Exists(dest) ? dest : "";
    }

    private static async void ShowLimitWarningPopup(string appName, int delaySeconds, string processName)
    {
        try
        {
            var showWarning = await _db!.GetShowWarningAsync();
            if (!showWarning) return;
            var language = await _db.GetSettingAsync("UiLanguage", "en");
            var warningMsg = await _db!.GetWarningMessageAsync();
            var detail = await GetLimitResetDetailAsync(appName, language);
            ShowWarningPopup(
                appName,
                delaySeconds,
                processName,
                Localization.Text("DailyLimitReached", language),
                string.IsNullOrWhiteSpace(warningMsg) ? Localization.Text("LimitReachedMessage", language, appName) : warningMsg,
                detail,
                language);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to show limit warning popup for {appName}: {ex.Message}");
        }
    }

    private static async void ShowScheduleWarningPopup(string appName)
    {
        try
        {
            var showWarning = await _db!.GetShowWarningAsync();
            if (!showWarning) return;
            var language = await _db.GetSettingAsync("UiLanguage", "en");
            var procName = _tracker?.GetProcessNameForApp(appName) ?? appName;
            var detail = await GetScheduleResetDetailAsync(appName, language);
            ShowWarningPopup(
                appName,
                0,
                procName,
                Localization.Text("OutsideAllowedHours", language),
                Localization.Text("ScheduleBlockedMessage", language, appName),
                detail,
                language);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to show schedule warning popup for {appName}: {ex.Message}");
        }
    }

    private static void ShowWarningPopup(
        string appName,
        int delaySeconds,
        string processName,
        string reason,
        string message,
        string detail,
        string language)
    {
        try
        {
            var popupPath = GetPopupHostPath();
            if (string.IsNullOrEmpty(popupPath)) return;

            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                appName,
                delay = delaySeconds,
                message,
                proc = processName,
                reason,
                detail,
                closingTemplate = Localization.Text("ClosingInSeconds", language),
                closedTemplate = Localization.Text("WasClosed", language),
                closingNowText = Localization.Text("ClosingNow", language)
            });
            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            Process.Start(new ProcessStartInfo
            {
                FileName = popupPath,
                Arguments = b64,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to launch warning popup for {appName}: {ex.Message}");
        }
    }

    private static async Task<string> GetLimitResetDetailAsync(string appName, string language)
    {
        try
        {
            var limits = await _db!.GetLimitRulesAsync();
            var limit = limits.FirstOrDefault(l => l.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));
            if (limit == null)
                return Localization.Text("CanUseAgainAfterMidnight", language);

            var bonus = await _db.GetTodayBonusMinutesAsync(appName);
            var total = limit.DailyMaxMinutes + bonus;
            var reset = DateTime.Today.AddDays(1);
            return Localization.Text("AllowanceResetsAt", language, total, reset);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to calculate limit reset detail for {appName}: {ex.Message}");
            return Localization.Text("CanUseAgainAfterMidnight", language);
        }
    }

    private static async Task<string> GetScheduleResetDetailAsync(string appName, string language)
    {
        try
        {
            if (_scheduler == null)
                return Localization.Text("CheckScheduleNextAllowed", language);

            var rules = await _scheduler.GetRulesAsync();
            var matching = rules
                .Where(r => r.Enabled &&
                    (string.IsNullOrWhiteSpace(r.AppName) ||
                     r.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var next = SchedulerService.GetNextAllowedTime(matching, DateTime.Now);
            return next.HasValue
                ? Localization.Text("NextAllowedTime", language, next.Value)
                : Localization.Text("CheckScheduleNextAllowed", language);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to calculate schedule reset detail for {appName}: {ex.Message}");
            return Localization.Text("CheckScheduleNextAllowed", language);
        }
    }

    public static object GetRuntimeHealth(AppConfig config, UsageDatabase db, EmailService emailService)
    {
        return new
        {
            watchdog = GetWatchdogStatus(),
            autoStart = new { enabled = GetAutoStart() },
            dashboard = new
            {
                bindAddress = config.EnableRemoteDashboard
                    ? (string.IsNullOrWhiteSpace(config.DashboardBindAddress) ? "0.0.0.0" : config.DashboardBindAddress)
                    : "127.0.0.1",
                port = DashboardServer.Port,
                remoteEnabled = config.EnableRemoteDashboard
            },
            email = new { configured = emailService.IsEnabled },
            database = new
            {
                path = db.DatabasePath,
                exists = File.Exists(db.DatabasePath)
            }
        };
    }

    private static object GetWatchdogStatus()
    {
        var path = GetWatchdogPath();
        var installed = false;
        var status = "not_installed";

        try
        {
            using var sc = new System.ServiceProcess.ServiceController(WatchdogServiceName);
            status = sc.Status.ToString();
            installed = true;
        }
        catch (Exception ex)
        {
            status = string.IsNullOrEmpty(path) ? "missing_executable" : $"not_available: {ex.Message}";
        }

        return new
        {
            installed,
            status,
            executablePath = path
        };
    }

    private static void OnAppKilled(string appName)
    {
        // Popup is handled by separate PopupHost process
    }

    private static async Task Cleanup()
    {
        Logger.Instance.Info("Monitor shutting down");
        _usageTracker?.Stop();
        _tracker?.Stop();
        DashboardServer.LoginLockoutDetected = null;
        _parentReportService?.Dispose();
        _emailService?.Dispose();
        await DashboardServer.StopAsync();
        _db?.Dispose();
        _cts?.Cancel();
        _instanceMutex?.Dispose();
    }
}
