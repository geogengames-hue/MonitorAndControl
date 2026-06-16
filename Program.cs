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
    private static HiddenForm? _hiddenForm;
    private static CancellationTokenSource? _cts;

    private static Mutex? _instanceMutex;

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

            var (mods, key) = HotKeyService.ParseHotKey(config.HotKeyModifiers, config.HotKeyKey);

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
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
            path = "appsettings.json";

        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            _cachedConfig = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            return _cachedConfig;
        }
        _cachedConfig = new AppConfig();
        return _cachedConfig;
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
        catch { return false; }
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
                    key.SetValue(AppName, $"\"{exePath}\"");
                    key.DeleteValue(LegacyAppName, false);
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
                key.DeleteValue(LegacyAppName, false);
            }
        }
        catch { }
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
                if (currentBinPath.Trim('"').Equals(watchdogPath, StringComparison.OrdinalIgnoreCase))
                    return; // Path matches, nothing to do

                Logger.Instance.Info("Watchdog path changed, updating service...");
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
            }
            catch { return; }
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
        try { Environment.Exit(0); } catch { }
    }

    private static async Task Initialize(AppConfig config)
    {
        _db = new UsageDatabase();
        await _db.InitializeDefaults(config);

        if (string.IsNullOrEmpty(await _db.GetSettingAsync("KillDelaySeconds", "")))
            await _db.SetKillDelayAsync(config.KillDelaySeconds);
        if (string.IsNullOrEmpty(await _db.GetSettingAsync("ShowWarning", "")))
            await _db.SetShowWarningAsync(config.ShowWarningOnChildPc);
    }

    private static void StartServices(AppConfig config)
    {
        _tracker = new WindowTracker();
        _tracker.LoadKnownApps(config.KnownApps);
        // Load dynamic app mappings from database
        var mappings = _db!.GetAppMappingsAsync().GetAwaiter().GetResult();
        foreach (var m in mappings)
            _tracker.AddKnownApp(m.ProcessName, m.AppName);
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
        _enforcer.OnBreachAlert += (app, delay, proc) => _ = _emailService.SendAlertAsync($"Limit Breach: {app}", $"{app} exceeded its daily limit. Closing in {delay}s.");
        _enforcer.OnAppKilled += (app) => _ = _emailService.SendAlertAsync($"App Closed: {app}", $"{app} was closed after exceeding its limit.");
        _enforcer.OnAppTerminatedBySchedule += (app) => _ = _emailService.SendAlertAsync($"Schedule Block: {app}", $"{app} was closed by schedule rule.");
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
                _db!, _tracker!, _enforcer!, _scheduler!, _emailService!, config);
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
            if (_emailService == null) return;
            var marker = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SystemHelper",
                "watchdog-restart.marker");
            if (!File.Exists(marker)) return;

            var markerText = await File.ReadAllTextAsync(marker);
            File.Delete(marker);
            await _emailService.SendAlertAsync(
                "Monitor Restarted by Watchdog",
                $"The monitor was restarted by the watchdog on {Environment.MachineName}.\n{markerText}");
        }
        catch
        {
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
            var warningMsg = await _db!.GetWarningMessageAsync();
            var detail = await GetLimitResetDetailAsync(appName);
            ShowWarningPopup(
                appName,
                delaySeconds,
                processName,
                "Daily limit reached",
                string.IsNullOrWhiteSpace(warningMsg) ? $"{appName} reached today's limit." : warningMsg,
                detail);
        }
        catch { }
    }

    private static async void ShowScheduleWarningPopup(string appName)
    {
        try
        {
            var showWarning = await _db!.GetShowWarningAsync();
            if (!showWarning) return;
            var procName = _tracker?.GetProcessNameForApp(appName) ?? appName;
            var detail = await GetScheduleResetDetailAsync(appName);
            ShowWarningPopup(
                appName,
                0,
                procName,
                "Outside allowed hours",
                $"{appName} is not allowed right now.",
                detail);
        }
        catch { }
    }

    private static void ShowWarningPopup(
        string appName,
        int delaySeconds,
        string processName,
        string reason,
        string message,
        string detail)
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
                detail
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
        catch { }
    }

    private static async Task<string> GetLimitResetDetailAsync(string appName)
    {
        try
        {
            var limits = await _db!.GetLimitRulesAsync();
            var limit = limits.FirstOrDefault(l => l.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));
            if (limit == null)
                return "The app can be used again after the limit resets at midnight.";

            var bonus = await _db.GetTodayBonusMinutesAsync(appName);
            var total = limit.DailyMaxMinutes + bonus;
            var reset = DateTime.Today.AddDays(1);
            return $"Today's allowance: {total} min. Resets at {reset:t}.";
        }
        catch
        {
            return "The app can be used again after the limit resets at midnight.";
        }
    }

    private static async Task<string> GetScheduleResetDetailAsync(string appName)
    {
        try
        {
            if (_scheduler == null)
                return "Check the dashboard schedule for the next allowed time.";

            var rules = await _scheduler.GetRulesAsync();
            var matching = rules
                .Where(r => r.Enabled &&
                    (string.IsNullOrWhiteSpace(r.AppName) ||
                     r.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var next = SchedulerService.GetNextAllowedTime(matching, DateTime.Now);
            return next.HasValue
                ? $"Next allowed time: {next.Value:g}."
                : "Check the dashboard schedule for the next allowed time.";
        }
        catch
        {
            return "Check the dashboard schedule for the next allowed time.";
        }
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
        _emailService?.Dispose();
        await DashboardServer.StopAsync();
        _db?.Dispose();
        _cts?.Cancel();
        _instanceMutex?.Dispose();
    }
}
