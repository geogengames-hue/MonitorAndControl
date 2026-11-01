using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

/// <summary>
/// Collects a read-only snapshot of how this install is running, for the dashboard's
/// Diagnostics panel. It deliberately never includes secrets: no passwords, no tokens,
/// and not the dashboard hotkey combination (only whether it is registered).
/// </summary>
public static class DiagnosticsService
{
    public static object Collect(UsageDatabase db)
    {
        int sessionId;
        using (var self = Process.GetCurrentProcess()) sessionId = self.SessionId;
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var update = ReadUpdateStatus(installDir);
        var channel = ReadUpdateChannel();
        var protectedInstall = IsInstallFolderProtected();

        return new
        {
            version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown",
            process = new
            {
                user = $"{Environment.UserDomainName}\\{Environment.UserName}",
                sessionId,
                isAdministrator = IsAdministrator()
            },
            install = new
            {
                directory = installDir,
                // True when this (standard-user) process cannot write to its own folder,
                // i.e. the child cannot modify or delete the app files.
                protectedFromChild = protectedInstall
            },
            config = new { source = Program.ConfigSource },
            hotkey = new
            {
                registered = HotKeyService.Registered,
                problem = HotKeyService.Registered ? "" : HotKeyService.DescribeError(HotKeyService.LastError)
            },
            updates = new
            {
                channel,
                lastStatus = update.Status,
                lastFinished = update.Finished,
                lastMessage = update.Message,
                // A protected install can only update through the SYSTEM service.
                dashboardUpdateAvailable = !protectedInstall || channel == "system"
            },
            screenTime = ReadScreenTime(),
            data = new
            {
                directory = AppPaths.DataDir,
                databaseKb = FileKb(db.DatabasePath),
                logKb = FileKb(AppPaths.LogPath)
            }
        };
    }

    /// <summary>Reads a JSON property ignoring case (the updater writes PascalCase, the dashboard camelCase).</summary>
    public static string? GetString(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
        return null;
    }

    public static (string Status, string Finished, string Message) ReadUpdateStatus(string installDir)
    {
        try
        {
            var path = Path.Combine(installDir, "update-status.json");
            if (!File.Exists(path)) return ("none", "", "");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return (
                GetString(doc.RootElement, "status") ?? "unknown",
                GetString(doc.RootElement, "finishedAt") ?? "",
                GetString(doc.RootElement, "message") ?? "");
        }
        catch
        {
            return ("unreadable", "", "");
        }
    }

    private static string ReadUpdateChannel()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir, "update-channel.json");
            if (!File.Exists(path)) return "local";
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return GetString(doc.RootElement, "mode") ?? "local";
        }
        catch
        {
            return "local";
        }
    }

    private static object ReadScreenTime()
    {
        string service;
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("GameHost");
            service = sc.Status.ToString();
        }
        catch
        {
            service = "NotInstalled";
        }

        double? ageSeconds = null;
        try
        {
            var path = ScreenTime.ScreenTimeFiles.StatusPath;
            if (File.Exists(path))
                ageSeconds = Math.Round((DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds);
        }
        catch { }

        return new
        {
            service,
            // The service refreshes its status every few seconds; an old file means it stopped.
            statusAgeSeconds = ageSeconds,
            working = service == "Running" && ageSeconds is { } age && age < 60
        };
    }

    /// <summary>True when this (standard-user) process cannot write to its own install folder.</summary>
    public static bool IsInstallFolderProtected()
    {
        // The probe creates a file, and this is called from a polled endpoint: cache the answer
        // briefly (folder permissions only change when the app is reinstalled).
        lock (ProtectedCacheLock)
        {
            if (_protectedCache is { } cached && DateTime.UtcNow - _protectedCachedAt < TimeSpan.FromSeconds(60))
                return cached;
            var result = !CanWriteTo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            _protectedCache = result;
            _protectedCachedAt = DateTime.UtcNow;
            return result;
        }
    }

    private static readonly object ProtectedCacheLock = new();
    private static bool? _protectedCache;
    private static DateTime _protectedCachedAt;

    /// <summary>Tries to create a throwaway file; failure means the folder is protected from this user.</summary>
    private static bool CanWriteTo(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static long FileKb(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length / 1024 : 0; }
        catch { return 0; }
    }
}
