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
    private static UsageDatabase? _db;
    private static WindowTracker? _tracker;
    private static UsageTracker? _usageTracker;
    private static LimitEnforcer? _enforcer;
    private static SchedulerService? _scheduler;
    private static NotificationService? _notifier;
    private static EmailService? _emailService;
    private static HiddenForm? _hiddenForm;
    private static CancellationTokenSource? _cts;

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        _cts = new CancellationTokenSource();

        Logger.Instance.Info($"Monitor started on {Environment.MachineName}");
        try
        {
            var config = LoadConfig();
            Initialize(config).GetAwaiter().GetResult();
            RegisterAutoStart();
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
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key != null)
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                    key.SetValue("SystemHelper", $"\"{exePath}\"");
            }
        }
        catch { }
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

        _scheduler = new SchedulerService(_db!);
        _scheduler.InvalidateCache();

        _enforcer = new LimitEnforcer(_db!, _tracker);
        _enforcer.OnBreachAlert += ShowWarningPopup;
        _enforcer.OnBreachAlert += (app, delay) => Logger.Instance.Info($"Limit breach: {app} — closing in {delay}s");
        _enforcer.OnAppKilled += OnAppKilled;
        _enforcer.OnAppKilled += (app) => Logger.Instance.Warn($"App killed: {app}");
        _enforcer.OnAppTerminatedBySchedule += (app) => Logger.Instance.Warn($"Schedule kill: {app}");

        _notifier = new NotificationService(_db!);
        _enforcer.OnBreachAlert += (app, delay) => _ = _notifier.NotifyBreachAsync(app, delay);
        _enforcer.OnAppKilled += (app) => _ = _notifier.NotifyKilledAsync(app);
        _enforcer.OnAppTerminatedBySchedule += (app) => _ = _notifier.NotifyScheduleTerminatedAsync(app);

        _emailService = new EmailService(_db!, _tracker!, _enforcer!, _scheduler!);
        _emailService.LoadSettingsAsync().GetAwaiter().GetResult();
        _emailService.StartPolling();
        _enforcer.OnBreachAlert += (app, delay) => _ = _emailService.SendAlertAsync($"Limit Breach: {app}", $"{app} exceeded its daily limit. Closing in {delay}s.");
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

    private static WarningPopup? _activePopup;

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

    private static async void ShowWarningPopup(string appName, int delaySeconds)
    {
        try
        {
            var showWarning = await _db!.GetShowWarningAsync();
            if (!showWarning) return;
            var warningMsg = await _db!.GetWarningMessageAsync();
            if (_hiddenForm != null && !_hiddenForm.IsDisposed)
            {
                _hiddenForm.BeginInvoke(() =>
                {
                    _activePopup?.Close();
                    _activePopup = new WarningPopup(appName, delaySeconds, warningMsg);
                    _activePopup.Show();
                });
            }
        }
        catch { }
    }

    private static void OnAppKilled(string appName)
    {
        try
        {
            if (_hiddenForm != null && !_hiddenForm.IsDisposed)
            {
                _hiddenForm.BeginInvoke(() =>
                {
                    _activePopup?.Close();
                    _activePopup = null;
                });
            }
        }
        catch { }
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
    }
}
