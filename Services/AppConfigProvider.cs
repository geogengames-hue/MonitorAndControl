using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using MonitorAndControl.Data;
using MonitorAndControl.Models;
using MonitorAndControl.Web;

namespace MonitorAndControl.Services;

public static class AppConfigProvider
{
    private const string AppName = "DeviceMon";
    private const string LegacyAppName = "MonitorAndControl";
    private const string WatchdogServiceName = "GameHost";

    private static AppConfig? _cachedConfig;

    public static string ConfigSource { get; private set; } = "built-in defaults";

    public static string CurrentExecutablePath =>
        Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(AppContext.BaseDirectory, "DeviceMon.exe");

    public static AppConfig GetConfig() => _cachedConfig ?? LoadConfig();

    public static AppConfig LoadConfig()
    {
        var path = GetConfigPath();
        var lkgPath = Path.Combine(AppPaths.DataDir, "appsettings.lkg.json");

        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<AppConfig>(json)
                    ?? throw new InvalidOperationException("Configuration deserialized to null.");
                _cachedConfig = config;
                ConfigSource = "appsettings.json";
                SaveLastKnownGoodConfig(lkgPath, json);
                return _cachedConfig;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"appsettings.json is invalid ({ex.Message}); falling back to last-known-good or defaults.");
            }
        }
        else
        {
            Logger.Instance.Warn("appsettings.json not found; using last-known-good configuration or defaults.");
        }

        if (File.Exists(lkgPath))
        {
            try
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(lkgPath));
                if (config != null)
                {
                    _cachedConfig = config;
                    ConfigSource = "last-known-good backup (appsettings.json was unreadable)";
                    Logger.Instance.Info("Loaded configuration from last-known-good backup.");
                    return _cachedConfig;
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error($"Last-known-good configuration is unusable ({ex.Message}); using defaults.");
            }
        }

        _cachedConfig = new AppConfig();
        return _cachedConfig;
    }

    private static void SaveLastKnownGoodConfig(string lkgPath, string json)
    {
        try
        {
            var dir = Path.GetDirectoryName(lkgPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(lkgPath, json);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to save last-known-good configuration: {ex.Message}");
        }
    }

    public static string GetConfigPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        return File.Exists(path) ? path : Path.GetFullPath("appsettings.json");
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
                var exePath = CurrentExecutablePath;
                if (!string.IsNullOrEmpty(exePath))
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

    public static async Task<(string Modifiers, string Key)> GetDashboardHotKeySettings(UsageDatabase db, AppConfig config)
    {
        var modifiers = await db.GetSettingAsync("HotKeyModifiers", config.HotKeyModifiers);
        var key = await db.GetSettingAsync("HotKeyKey", config.HotKeyKey);
        return (modifiers, key);
    }

    public static async Task<bool> UpdateDashboardHotKeyAsync(UsageDatabase db, string modifiers, string key)
    {
        if (!HotKeyService.TryParseHotKey(modifiers, key, out _, out _, out _, out _, out _))
            return false;

        await db.SetSettingAsync("HotKeyModifiers", modifiers);
        await db.SetSettingAsync("HotKeyKey", key);
        return true;
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
            },
            diagnostics = DiagnosticsService.Collect(db)
        };
    }

    private static object GetWatchdogStatus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "GameHost.exe");
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
            status = !File.Exists(path) ? "missing_executable" : $"not_available: {ex.Message}";
        }

        return new
        {
            installed,
            status,
            executablePath = path
        };
    }

    public static void Shutdown(int delayMs = 0)
    {
        if (delayMs > 0)
        {
            Task.Run(async () =>
            {
                await Task.Delay(delayMs);
                Environment.Exit(0);
            });
        }
        else
        {
            Environment.Exit(0);
        }
    }
}
