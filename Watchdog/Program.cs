using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;

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
            if (IsUpdateInProgress())
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
                    var commandLine = $"\"{_options.MonitorPath}\"";
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

internal sealed record WatchdogOptions(string MonitorPath, int IntervalSeconds, string DataDirectory)
{
    public string LogPath => Path.Combine(DataDirectory, "watchdog.log");
    public string RestartMarkerPath => Path.Combine(DataDirectory, "watchdog-restart.marker");
    public string UpdateMarkerPath => Path.Combine(DataDirectory, "Protected", "update-in-progress.marker");

    public static WatchdogOptions Parse(string[] args)
    {
        var monitorPath = Path.Combine(AppContext.BaseDirectory, Program.MonitorExeName);
        var interval = 15;
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
