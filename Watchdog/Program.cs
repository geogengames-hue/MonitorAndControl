using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace MonitorAndControlWatchdog;

internal static class Program
{
    internal const string ServiceName = "GameHost";
    internal const string LegacyServiceName = "MonitorAndControlWatchdog";
    internal const string OlderServiceName = "SystemHelperWatchdog";
    internal const string MonitorExeName = "DeviceMon.exe";
    internal const string WatchdogExeName = "GameHost.exe";
    internal const string LegacyMonitorExeName = "MonitorAndControl.exe";
    internal const string LegacyWatchdogExeName = "MonitorAndControlWatchdog.exe";
    internal const string OlderMonitorExeName = "SystemHelper.exe";
    internal const string OlderWatchdogExeName = "SystemHelperWatchdog.exe";

    public static void Main(string[] args)
    {
        if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
        {
            InstallService(args);
            return;
        }
        if (args.Contains("--update", StringComparer.OrdinalIgnoreCase))
        {
            UpdateService(args);
            return;
        }
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            UninstallService();
            return;
        }

        var options = WatchdogOptions.Parse(args);
        if (args.Contains("--console", StringComparer.OrdinalIgnoreCase) || Environment.UserInteractive)
        {
            using var watchdog = new WatchdogService(options);
            watchdog.StartForConsole();
            Console.WriteLine("GameHost watchdog running. Press Enter to stop.");
            Console.ReadLine();
            watchdog.StopForConsole();
            return;
        }

        ServiceBase.Run(new WatchdogService(options));
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool SuppressElevation(string[] args)
    {
        return args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase) ||
               string.Equals(Environment.GetEnvironmentVariable("DEVICEMON_SUPPRESS_WATCHDOG_UAC"), "1", StringComparison.Ordinal);
    }

    private static void InstallService(string[] args)
    {
        if (!IsElevated())
        {
            if (SuppressElevation(args))
            {
                Console.Error.WriteLine("Watchdog install skipped because elevation is disabled for this run.");
                Environment.ExitCode = 5;
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                UseShellExecute = true,
                Verb = "runas"
            };
            try { Process.Start(psi)?.WaitForExit(); }
            catch { }
            return;
        }

        var options = WatchdogOptions.Parse(args);
        PrepareDataDirectory(options.DataDirectory);
        var exePath = Path.Combine(AppContext.BaseDirectory, WatchdogExeName);
        if (!File.Exists(exePath))
            exePath = options.MonitorPath.Replace(MonitorExeName, WatchdogExeName);
        if (!File.Exists(exePath))
            exePath = options.MonitorPath.Replace(LegacyMonitorExeName, LegacyWatchdogExeName);
        if (!File.Exists(exePath))
            exePath = options.MonitorPath.Replace(OlderMonitorExeName, OlderWatchdogExeName);

        RunSc($"stop {LegacyServiceName}");
        RunSc($"delete {LegacyServiceName}");
        RunSc($"stop {OlderServiceName}");
        RunSc($"delete {OlderServiceName}");
        RunSc($"stop {ServiceName}");
        RunSc($"delete {ServiceName}");
        var scCreate = $"create {ServiceName} binPath= \"{exePath}\" start= auto DisplayName= \"GameHost\"";
        RunSc(scCreate);
        RunSc($"description {ServiceName} \"Restarts DeviceMon.exe if it is stopped.\"");
        ConfigureServiceRecovery();
        Thread.Sleep(1000);
        RunSc($"start {ServiceName}");
        Console.WriteLine("Watchdog service installed and started.");
    }

    private static void UpdateService(string[] args)
    {
        if (!IsElevated())
        {
            if (SuppressElevation(args))
            {
                Console.Error.WriteLine("Watchdog update skipped because elevation is disabled for this run.");
                Environment.ExitCode = 5;
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                UseShellExecute = true,
                Verb = "runas"
            };
            try { Process.Start(psi)?.WaitForExit(); }
            catch { }
            return;
        }

        var options = WatchdogOptions.Parse(args);
        PrepareDataDirectory(options.DataDirectory);
        var exePath = Path.Combine(AppContext.BaseDirectory, WatchdogExeName);
        if (!File.Exists(exePath))
            exePath = options.MonitorPath.Replace(MonitorExeName, WatchdogExeName);
        if (!File.Exists(exePath))
            exePath = options.MonitorPath.Replace(LegacyMonitorExeName, LegacyWatchdogExeName);
        if (!File.Exists(exePath))
            exePath = options.MonitorPath.Replace(OlderMonitorExeName, OlderWatchdogExeName);

        RunSc($"config {ServiceName} binPath= \"{exePath}\"");
        ConfigureServiceRecovery();
        RunSc($"start {ServiceName}");
        Console.WriteLine("Watchdog service path updated and started.");
    }

    private static void UninstallService()
    {
        if (!IsElevated())
        {
            if (string.Equals(Environment.GetEnvironmentVariable("DEVICEMON_SUPPRESS_WATCHDOG_UAC"), "1", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Watchdog uninstall skipped because elevation is disabled for this run.");
                Environment.ExitCode = 5;
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = "--uninstall",
                UseShellExecute = true,
                Verb = "runas"
            };
            try { Process.Start(psi)?.WaitForExit(); }
            catch { }
            return;
        }

        // Release the deny-delete protection so an administrator can manage or
        // reset the data now that the watchdog will no longer re-assert it.
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SystemHelper");
        UnprotectFile(Path.Combine(dataDir, "monitor.db"));
        UnprotectFile(Path.Combine(dataDir, "events.log"));

        RunSc($"stop {ServiceName}");
        RunSc($"stop {LegacyServiceName}");
        RunSc($"stop {OlderServiceName}");
        Thread.Sleep(1000);
        RunSc($"delete {ServiceName}");
        RunSc($"delete {LegacyServiceName}");
        RunSc($"delete {OlderServiceName}");
        Console.WriteLine("Watchdog service removed.");
    }

    private static void RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("sc", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
        }
        catch { }
    }

    private static void ConfigureServiceRecovery()
    {
        RunSc($"failure {ServiceName} reset= 60 actions= restart/5000/restart/5000/restart/5000");
        RunSc($"failureflag {ServiceName} 1");
    }

    internal static void PrepareDataDirectory(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var dirInfo = new DirectoryInfo(dataDirectory);
            var security = dirInfo.GetAccessControl();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var rule = new FileSystemAccessRule(
                users,
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);
            security.AddAccessRule(rule);
            dirInfo.SetAccessControl(security);
        }
        catch
        {
        }
        ProtectUpdateMarkerDirectory(Path.Combine(dataDirectory, "Protected"));
    }

    internal static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    internal static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    internal static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    // S-1-3-4 = OWNER RIGHTS. Present in a DACL, it overrides the implicit rights
    // an owner normally gets (READ_CONTROL + WRITE_DAC). We use it to stop the
    // child - who created and therefore owns monitor.db / events.log - from
    // rewriting the file's ACL to grant themselves Delete back.
    internal static readonly SecurityIdentifier OwnerRightsSid = new("S-1-3-4");

    /// <summary>
    /// Locks a single data file so standard users can read and write it (the child's
    /// DeviceMon still records usage and appends logs) but cannot delete or rename it,
    /// and cannot re-grant themselves delete via file ownership. SYSTEM and
    /// Administrators keep full control so the watchdog can re-assert this each cycle.
    /// </summary>
    internal static void HardenFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            var fi = new FileInfo(path);
            var security = new FileSecurity();
            // Drop inherited permissions (the parent folder grants Users Modify,
            // which includes Delete) and replace with an explicit, minimal set.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            security.AddAccessRule(new FileSystemAccessRule(
                SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                AdminsSid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                UsersSid, FileSystemRights.ReadAndExecute | FileSystemRights.Write, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                OwnerRightsSid, FileSystemRights.ReadAndExecute | FileSystemRights.Write, AccessControlType.Allow));
            // Explicit deny defeats the parent folder's FILE_DELETE_CHILD grant.
            security.AddAccessRule(new FileSystemAccessRule(
                UsersSid,
                FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
                AccessControlType.Deny));

            fi.SetAccessControl(security);
        }
        catch
        {
            // Best-effort; the file may briefly be unavailable. Re-tried next cycle.
        }
    }

    /// <summary>
    /// Removes the deny-delete hardening from a data file and restores normal
    /// inheritance from the parent folder. Called during uninstall (elevated) so an
    /// administrator can manage or reset the data once GameHost is gone.
    /// </summary>
    internal static void UnprotectFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            var fi = new FileInfo(path);
            var security = fi.GetAccessControl();
            // Re-enable inheritance and strip the explicit ACEs we added.
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                security.RemoveAccessRule(rule);
            fi.SetAccessControl(security);
        }
        catch
        {
        }
    }

    private static void ProtectUpdateMarkerDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        catch
        {
        }
    }
}

internal sealed class WatchdogService : ServiceBase
{
    private readonly WatchdogOptions _options;
    private readonly System.Threading.Timer _timer;
    private bool _hadSeenMonitor;
    private int _checking;
    private DateTime _lastProtectUtc = DateTime.MinValue;
    private DateTime _lastUpdateKickUtc = DateTime.MinValue;

    private string TriggerPath => Path.Combine(_options.DataDirectory, "update.trigger");
    private string ChannelFlagPath => Path.Combine(_options.DataDirectory, "update-channel.json");
    private string UpdateSourceConfigPath => Path.Combine(_options.DataDirectory, "Protected", "update-source.json");

    public WatchdogService(WatchdogOptions options)
    {
        _options = options;
        ServiceName = Program.ServiceName;
        CanStop = true;
        CanShutdown = true;
        _timer = new System.Threading.Timer(CheckMonitor, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void StartForConsole() => OnStart([]);
    public void StopForConsole() => OnStop();

    protected override void OnStart(string[] args)
    {
        Program.PrepareDataDirectory(_options.DataDirectory);
        ProtectAndMaintainData(force: true, updateInProgress: IsUpdateInProgress());
        Log($"Watchdog started. Monitor={_options.MonitorPath}");
        _timer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(_options.IntervalSeconds));
    }

    protected override void OnStop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        Log("Watchdog stopped.");
    }

    private void CheckMonitor(object? state)
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        try
        {
            var updating = IsUpdateInProgress();

            // Re-assert file protection and restore any missing data / executable
            // before deciding whether the monitor needs relaunching. The executable
            // is left alone during updates (the UpdateAgent is swapping it out).
            ProtectAndMaintainData(force: false, updateInProgress: updating);

            // Advertise whether a trusted (SYSTEM) update source is configured, and
            // service a dashboard-requested update from that source.
            MaintainUpdateChannel();
            if (!updating)
                ProcessUpdateTrigger();

            if (updating)
            {
                Log("Update marker present; monitor restart paused.");
                return;
            }

            if (IsMonitorRunning())
            {
                _hadSeenMonitor = true;
                return;
            }

            if (_hadSeenMonitor)
                WriteRestartMarker("Monitor process was missing and watchdog relaunched it.");

            StartMonitorInActiveSession();
            _hadSeenMonitor = true;
        }
        catch (Exception ex)
        {
            Log("Check failed: " + ex);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private bool IsUpdateInProgress()
    {
        try
        {
            if (!File.Exists(_options.UpdateMarkerPath))
                return false;

            var age = DateTimeOffset.Now - File.GetLastWriteTime(_options.UpdateMarkerPath);
            if (age <= TimeSpan.FromMinutes(15))
                return true;

            File.Delete(_options.UpdateMarkerPath);
            Log("Removed stale update marker.");
            return false;
        }
        catch (Exception ex)
        {
            Log("Failed to inspect update marker: " + ex.Message);
            return false;
        }
    }

    private bool IsMonitorRunning()
    {
        var expected = Path.GetFullPath(_options.MonitorPath);
        var processName = Path.GetFileNameWithoutExtension(expected);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path != null && Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                return true;
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private void StartMonitorInActiveSession()
    {
        if (!File.Exists(_options.MonitorPath))
        {
            Log($"Monitor executable not found: {_options.MonitorPath}");
            return;
        }

        var sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            Log("No active console session.");
            return;
        }

        if (!NativeMethods.WTSQueryUserToken(sessionId, out var userToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed");

        try
        {
            var sa = new NativeMethods.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>() };
            if (!NativeMethods.DuplicateTokenEx(
                    userToken,
                    NativeMethods.TOKEN_ALL_ACCESS,
                    ref sa,
                    NativeMethods.SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
                    NativeMethods.TOKEN_TYPE.TokenPrimary,
                    out var primaryToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed");

            try
            {
                if (!NativeMethods.CreateEnvironmentBlock(out var environment, primaryToken, false))
                    environment = IntPtr.Zero;

                try
                {
                    var startupInfo = new NativeMethods.STARTUPINFO();
                    startupInfo.cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>();
                    startupInfo.lpDesktop = "winsta0\\default";

                    var processInfo = new NativeMethods.PROCESS_INFORMATION();
                    var commandLine = $"\"{_options.MonitorPath}\" --autostart";
                    var workingDirectory = Path.GetDirectoryName(_options.MonitorPath) ?? AppContext.BaseDirectory;

                    var created = NativeMethods.CreateProcessAsUser(
                        primaryToken,
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                        environment,
                        workingDirectory,
                        ref startupInfo,
                        out processInfo);

                    if (!created)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed");

                    NativeMethods.CloseHandle(processInfo.hThread);
                    NativeMethods.CloseHandle(processInfo.hProcess);
                    Log("Monitor launched in active user session.");
                }
                finally
                {
                    if (environment != IntPtr.Zero)
                        NativeMethods.DestroyEnvironmentBlock(environment);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(primaryToken);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(userToken);
        }
    }

    /// <summary>
    /// Keeps the tamper-sensitive files protected and recoverable:
    ///   * restores monitor.db / DeviceMon.exe from the SYSTEM-only backup if the
    ///     live copy has gone missing (belt-and-suspenders behind the deny-delete ACL),
    ///   * re-applies the deny-delete hardening to monitor.db and events.log,
    ///   * refreshes the protected backups.
    /// The heavy work (ACL writes, file copies) is throttled; the cheap
    /// "restore if missing" check runs every cycle.
    /// </summary>
    /// <summary>
    /// Publishes a small, non-secret flag telling the (standard-user) dashboard
    /// whether quiet SYSTEM updates are available. "system" means a trusted update
    /// source is configured and the dashboard should queue updates through the
    /// watchdog; "local" means it should use the in-process updater as before.
    /// </summary>
    private void MaintainUpdateChannel()
    {
        try
        {
            var mode = File.Exists(UpdateSourceConfigPath) ? "system" : "local";
            var json = "{\"mode\":\"" + mode + "\"}";
            if (!File.Exists(ChannelFlagPath) || File.ReadAllText(ChannelFlagPath) != json)
                File.WriteAllText(ChannelFlagPath, json);
        }
        catch
        {
        }
    }

    /// <summary>
    /// If the dashboard dropped an update trigger, run an update from the
    /// pre-configured trusted source. The trigger carries no source data, so a
    /// forged trigger can only cause a legitimate update - it cannot redirect where
    /// the update is pulled from.
    /// </summary>
    private void ProcessUpdateTrigger()
    {
        try
        {
            if (!File.Exists(TriggerPath))
                return;

            // Consume the trigger no matter what, so it cannot loop.
            try { File.Delete(TriggerPath); } catch { }

            if ((DateTime.UtcNow - _lastUpdateKickUtc) < TimeSpan.FromSeconds(90))
            {
                Log("Update trigger ignored (rate limited).");
                return;
            }

            if (!File.Exists(UpdateSourceConfigPath))
            {
                Log("Update requested but no trusted update source is configured; ignoring.");
                return;
            }

            _lastUpdateKickUtc = DateTime.UtcNow;
            KickOffTrustedUpdate();
        }
        catch (Exception ex)
        {
            Log("Failed to process update trigger: " + ex.Message);
        }
    }

    private void KickOffTrustedUpdate()
    {
        var cfg = JsonSerializer.Deserialize<UpdateSourceConfig>(
            File.ReadAllText(UpdateSourceConfigPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.Source))
        {
            Log("Trusted update source config is empty; ignoring update request.");
            return;
        }

        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var updaterSrc = Path.Combine(installDir, "UpdateAgent.exe");
        if (!File.Exists(updaterSrc))
        {
            Log("UpdateAgent.exe not found next to the watchdog; cannot run update.");
            return;
        }

        // Stage the updater and its request in a SYSTEM-only working directory the
        // child cannot read or tamper with.
        var workDir = Path.Combine(_options.DataDirectory, "Protected", "updater");
        Directory.CreateDirectory(workDir);
        var updaterExe = Path.Combine(workDir, "UpdateAgent.exe");
        File.Copy(updaterSrc, updaterExe, overwrite: true);

        var requestPath = Path.Combine(workDir, "update-request.json");
        var request = new
        {
            source = cfg.Source,
            targetDirectory = installDir,
            monitorPath = _options.MonitorPath,
            monitorPid = (int?)null,
            restart = false, // GameHost relaunches DeviceMon in the child session afterwards.
            username = string.IsNullOrWhiteSpace(cfg.Username) ? null : cfg.Username,
            password = string.IsNullOrWhiteSpace(cfg.Password) ? null : cfg.Password,
            sha256 = string.IsNullOrWhiteSpace(cfg.Sha256) ? null : cfg.Sha256
        };
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request));

        // Pause monitor relaunch across the update (this watchdog and the one that
        // the updater restarts both honour this marker).
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_options.UpdateMarkerPath)!);
            File.WriteAllText(_options.UpdateMarkerPath, $"{DateTimeOffset.Now:O}|GameHost-update");
        }
        catch (Exception ex)
        {
            Log("Failed to write update marker before update: " + ex.Message);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = updaterExe,
            Arguments = $"--request \"{requestPath}\"",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        Log($"Started SYSTEM update from trusted source: {cfg.Source}");
    }

    private void ProtectAndMaintainData(bool force, bool updateInProgress)
    {
        try
        {
            var dbPath = Path.Combine(_options.DataDirectory, "monitor.db");
            var logPath = Path.Combine(_options.DataDirectory, "events.log");
            var protectedDir = Path.Combine(_options.DataDirectory, "Protected");
            Directory.CreateDirectory(protectedDir);

            // Cheap every-cycle safety net: put back anything that disappeared.
            RestoreIfMissing(Path.Combine(protectedDir, "monitor.db.bak"), dbPath, "monitor.db");
            if (!updateInProgress)
                RestoreIfMissing(Path.Combine(protectedDir, "DeviceMon.exe.bak"), _options.MonitorPath, "DeviceMon.exe");

            var now = DateTime.UtcNow;
            if (!force && (now - _lastProtectUtc) < TimeSpan.FromSeconds(60))
                return;
            _lastProtectUtc = now;

            Program.HardenFile(dbPath);
            Program.HardenFile(logPath);
            Program.HardenFile(Path.Combine(_options.DataDirectory, "appsettings.lkg.json"));

            BackupFile(dbPath, Path.Combine(protectedDir, "monitor.db.bak"));
            BackupFile(logPath, Path.Combine(protectedDir, "events.log.bak"));
            if (!updateInProgress)
                BackupFile(_options.MonitorPath, Path.Combine(protectedDir, "DeviceMon.exe.bak"));
        }
        catch (Exception ex)
        {
            Log("Data protection cycle failed: " + ex.Message);
        }
    }

    private void RestoreIfMissing(string backupPath, string livePath, string label)
    {
        try
        {
            if (string.IsNullOrEmpty(livePath) || File.Exists(livePath) || !File.Exists(backupPath))
                return;

            var dir = Path.GetDirectoryName(livePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.Copy(backupPath, livePath, overwrite: false);
            Log($"Restored missing {label} from protected backup.");
            WriteRestartMarker($"{label} was missing and the watchdog restored it from the protected backup.");
        }
        catch (Exception ex)
        {
            Log($"Failed to restore {label}: " + ex.Message);
        }
    }

    private static void BackupFile(string livePath, string backupPath)
    {
        try
        {
            if (string.IsNullOrEmpty(livePath) || !File.Exists(livePath)) return;
            if (File.Exists(backupPath) &&
                File.GetLastWriteTimeUtc(livePath) <= File.GetLastWriteTimeUtc(backupPath))
                return;

            File.Copy(livePath, backupPath, overwrite: true);
        }
        catch
        {
            // The file may be momentarily locked (e.g. mid-write); retried next cycle.
        }
    }

    private void WriteRestartMarker(string reason)
    {
        try
        {
            Directory.CreateDirectory(_options.DataDirectory);
            File.WriteAllText(_options.RestartMarkerPath, $"{DateTime.Now:O}|{reason}");
            Log(reason);
        }
        catch (Exception ex)
        {
            Log("Failed to write restart marker: " + ex.Message);
        }
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(_options.DataDirectory);
            File.AppendAllText(_options.LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
        }
    }
}

internal sealed class UpdateSourceConfig
{
    public string Source { get; set; } = "";
    public string? Sha256 { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

internal sealed record WatchdogOptions(string MonitorPath, int IntervalSeconds, string DataDirectory)
{
    public string LogPath => Path.Combine(DataDirectory, "watchdog.log");
    public string RestartMarkerPath => Path.Combine(DataDirectory, "watchdog-restart.marker");
    public string UpdateMarkerPath => Path.Combine(DataDirectory, "Protected", "update-in-progress.marker");

    public static WatchdogOptions Parse(string[] args)
    {
        var monitorPath = Path.Combine(AppContext.BaseDirectory, Program.MonitorExeName);
        var interval = 5;
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SystemHelper");

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--monitor", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                monitorPath = args[++i];
            else if (args[i].Equals("--interval", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out var parsed))
                interval = Math.Clamp(parsed, 5, 300);
            else if (args[i].Equals("--data", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                dataDirectory = args[++i];
        }

        return new WatchdogOptions(monitorPath, interval, dataDirectory);
    }
}

internal static class NativeMethods
{
    internal const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const uint TOKEN_ALL_ACCESS = 0xF01FF;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    internal enum SECURITY_IMPERSONATION_LEVEL
    {
        SecurityAnonymous,
        SecurityIdentification,
        SecurityImpersonation,
        SecurityDelegation
    }

    internal enum TOKEN_TYPE
    {
        TokenPrimary = 1,
        TokenImpersonation
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        ref SECURITY_ATTRIBUTES tokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel,
        TOKEN_TYPE tokenType,
        out IntPtr duplicateToken);

    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        int creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);
}
