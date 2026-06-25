using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace MonitorAndControl.UpdateAgent;

internal static class Program
{
    private const string MonitorExeName = "DeviceMon.exe";
    private const string WatchdogExeName = "GameHost.exe";
    private const string ServiceName = "GameHost";
    private static readonly JsonSerializerOptions StatusJsonOptions = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var options = UpdateOptions.Parse(args);
        if (options == null)
        {
            Console.Error.WriteLine("Usage: UpdateAgent --source <folder-or-https-zip-url> --target <install-folder> --monitor <DeviceMon.exe> [--sha256 <hash>] [--pid <pid>] [--restart]");
            return 2;
        }

        var logPath = Path.Combine(options.TargetDirectory, "update.log");
        var statusPath = Path.Combine(options.TargetDirectory, "update-status.json");
        void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(options.TargetDirectory);
                File.AppendAllText(logPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        var startedAt = DateTimeOffset.Now;
        try
        {
            Log($"Update started. Source={options.Source}");
            WriteStatus(statusPath, new UpdateStatus(
                "running",
                options.Source,
                startedAt,
                null,
                null,
                logPath));
            ConnectNetworkShare(options, Log);
            WriteUpdateMarker(Log);
            StopWatchdog(Log);
            await WaitForMonitorExitAsync(options.MonitorPid, options.MonitorPath, Log);

            var preparedSource = await PrepareSourceAsync(options.Source, options.ExpectedSha256, Log);
            ValidateSource(preparedSource);
            CopyDirectory(preparedSource, options.TargetDirectory, Log);

            RepairWatchdog(options.TargetDirectory, options.MonitorPath, Log);
            if (options.Restart)
                StartMonitor(options.MonitorPath, Log);

            Log("Update completed.");
            WriteStatus(statusPath, new UpdateStatus(
                "success",
                options.Source,
                startedAt,
                DateTimeOffset.Now,
                "Update completed successfully.",
                logPath));
            return 0;
        }
        catch (Exception ex)
        {
            Log("Update failed: " + ex);
            WriteStatus(statusPath, new UpdateStatus(
                "failed",
                options.Source,
                startedAt,
                DateTimeOffset.Now,
                ex.Message,
                logPath));
            TryRestartMonitorAfterFailure(options, Log);
            return 1;
        }
        finally
        {
            ClearUpdateMarker();
            options.DeleteRequestFile();
        }
    }

    private static void WriteStatus(string path, UpdateStatus status)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(status, StatusJsonOptions));
        }
        catch
        {
        }
    }

    private static void TryRestartMonitorAfterFailure(UpdateOptions options, Action<string> log)
    {
        if (!options.Restart)
            return;

        try
        {
            if (File.Exists(options.MonitorPath))
            {
                StartMonitor(options.MonitorPath, log);
                log("Monitor restarted after failed update.");
            }
        }
        catch (Exception restartEx)
        {
            log("Failed to restart monitor after failed update: " + restartEx.Message);
        }
    }

    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SystemHelper");

    private static string ProtectedDataDirectory => Path.Combine(DataDirectory, "Protected");
    private static string UpdateMarkerPath => Path.Combine(ProtectedDataDirectory, "update-in-progress.marker");

    private static void WriteUpdateMarker(Action<string> log)
    {
        try
        {
            PrepareProtectedDataDirectory();
            File.WriteAllText(UpdateMarkerPath, $"{DateTimeOffset.Now:O}|UpdateAgent");
            log($"Wrote update marker: {UpdateMarkerPath}");
        }
        catch (Exception ex)
        {
            log($"Failed to write update marker: {ex.Message}");
        }
    }

    private static void PrepareProtectedDataDirectory()
    {
        Directory.CreateDirectory(ProtectedDataDirectory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(ProtectedDataDirectory).SetAccessControl(security);
    }

    private static void ClearUpdateMarker()
    {
        try { File.Delete(UpdateMarkerPath); } catch { }
    }

    private static void ConnectNetworkShare(UpdateOptions options, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(options.Username) || !TryGetUncRoot(options.Source, out var uncRoot))
            return;

        var resource = new NativeMethods.NETRESOURCE
        {
            dwType = NativeMethods.RESOURCETYPE_DISK,
            lpRemoteName = uncRoot
        };

        var result = NativeMethods.WNetAddConnection2(ref resource, options.Password ?? "", options.Username, 0);
        if (result == 0)
        {
            log($"Connected to update share {uncRoot} as {options.Username}.");
            return;
        }

        if (result == 1219)
        {
            log($"Existing SMB connection conflicts with {uncRoot}; clearing connections to that server.");
            DisconnectExistingConnections(options.Source, uncRoot, log);
            result = NativeMethods.WNetAddConnection2(ref resource, options.Password ?? "", options.Username, 0);
            if (result == 0)
            {
                log($"Reconnected to update share {uncRoot} as {options.Username}.");
                return;
            }
        }

        throw new InvalidOperationException($"Could not connect to update share {uncRoot}. Windows error {result}.");
    }

    private static void DisconnectExistingConnections(string source, string uncRoot, Action<string> log)
    {
        NativeMethods.WNetCancelConnection2(uncRoot, 0, true);
        if (!TryGetUncServer(source, out var uncServer))
            return;

        RunProcess("net.exe", $"use {uncRoot} /delete /y", TimeSpan.FromSeconds(10), log);
        RunProcess("net.exe", $"use {uncServer}\\IPC$ /delete /y", TimeSpan.FromSeconds(10), log);
        RunProcess("net.exe", $"use {uncServer}\\* /delete /y", TimeSpan.FromSeconds(10), log);
    }

    private static bool TryGetUncRoot(string source, out string uncRoot)
    {
        uncRoot = "";
        if (!source.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        var parts = source.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        uncRoot = $@"\\{parts[0]}\{parts[1]}";
        return true;
    }

    private static bool TryGetUncServer(string source, out string uncServer)
    {
        uncServer = "";
        if (!source.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        var parts = source.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1)
            return false;

        uncServer = $@"\\{parts[0]}";
        return true;
    }

    private static async Task<string> PrepareSourceAsync(string source, string? expectedSha256, Action<string> log)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64 || expectedSha256.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidOperationException("HTTPS updates require a valid SHA-256 hash.");
            var tempRoot = Path.Combine(Path.GetTempPath(), "DeviceMonUpdate", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var zipPath = Path.Combine(tempRoot, "update.zip");
            log($"Downloading update ZIP from {uri}");
            using var http = new HttpClient();
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(zipPath))
                await input.CopyToAsync(output);

            await using (var package = File.OpenRead(zipPath))
            {
                var actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(package));
                if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Update ZIP SHA-256 mismatch. Expected {expectedSha256}, got {actualSha256}.");
                log($"Verified update ZIP SHA-256: {actualSha256}");
            }

            var extractPath = Path.Combine(tempRoot, "extract");
            ZipFile.ExtractToDirectory(zipPath, extractPath);
            return FindPackageRoot(extractPath);
        }

        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(source));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Update source folder not found: {fullPath}");

        return FindPackageRoot(fullPath);
    }

    private static string FindPackageRoot(string path)
    {
        if (File.Exists(Path.Combine(path, MonitorExeName)))
            return path;

        var candidates = Directory.GetFiles(path, MonitorExeName, SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.Count == 1
            ? candidates[0]!
            : path;
    }

    private static void ValidateSource(string sourceDirectory)
    {
        if (!File.Exists(Path.Combine(sourceDirectory, MonitorExeName)))
            throw new InvalidOperationException($"Update package must contain {MonitorExeName}.");
    }

    private static async Task WaitForMonitorExitAsync(int? pid, string monitorPath, Action<string> log)
    {
        if (pid.HasValue)
        {
            try
            {
                using var process = Process.GetProcessById(pid.Value);
                log($"Waiting for monitor process {pid.Value} to exit.");
                if (!process.WaitForExit(45000))
                {
                    log("Monitor did not exit in time; killing it.");
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                return;
            }
            catch (ArgumentException)
            {
                return;
            }
        }

        var processName = Path.GetFileNameWithoutExtension(monitorPath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.MainModule?.FileName?.Equals(monitorPath, StringComparison.OrdinalIgnoreCase) == true)
                {
                    log($"Waiting for monitor process {process.Id} to exit.");
                    if (!process.WaitForExit(45000))
                        process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        await Task.Delay(1000);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory, Action<string> log)
    {
        Directory.CreateDirectory(targetDirectory);
        var sourceWwwroot = Path.Combine(sourceDirectory, "wwwroot");
        var targetWwwroot = Path.Combine(targetDirectory, "wwwroot");
        if (Directory.Exists(sourceWwwroot) && Directory.Exists(targetWwwroot))
        {
            log("Replacing wwwroot web assets.");
            Directory.Delete(targetWwwroot, recursive: true);
        }

        var directories = Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories);
        foreach (var directory in directories)
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
        }

        var copied = 0;
        var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var target = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            for (var attempt = 1; attempt <= 8; attempt++)
            {
                try
                {
                    File.Copy(file, target, overwrite: true);
                    copied++;
                    log($"Copied: {relative}");
                    break;
                }
                catch (Exception ex) when (IsSkippableLockedWatchdogFile(relative, ex))
                {
                    log($"Skipped locked watchdog file: {relative} ({ex.Message})");
                    break;
                }
                catch (IOException) when (attempt < 8)
                {
                    Thread.Sleep(1000);
                }
                catch (UnauthorizedAccessException) when (attempt < 8)
                {
                    Thread.Sleep(1000);
                }
            }
        }

        log($"Copied {copied}/{files.Length} files and {directories.Length} directories from {sourceDirectory} to {targetDirectory}.");
    }

    private static bool IsSkippableLockedWatchdogFile(string relativePath, Exception ex)
    {
        var fileName = Path.GetFileName(relativePath);
        return (ex is IOException or UnauthorizedAccessException) &&
               fileName.StartsWith("GameHost.", StringComparison.OrdinalIgnoreCase);
    }

    private static void StopWatchdog(Action<string> log)
    {
        RunProcess("sc.exe", $"stop {ServiceName}", TimeSpan.FromSeconds(20), log);
    }

    private static void RepairWatchdog(string targetDirectory, string monitorPath, Action<string> log)
    {
        var watchdogPath = Path.Combine(targetDirectory, WatchdogExeName);
        if (!File.Exists(watchdogPath))
        {
            log("Watchdog executable not found after update; skipping service repair.");
            return;
        }

        RunProcess(watchdogPath, $"--update --no-elevate --monitor \"{monitorPath}\"", TimeSpan.FromSeconds(45), log);
    }

    private static void StartMonitor(string monitorPath, Action<string> log)
    {
        if (!File.Exists(monitorPath))
            throw new FileNotFoundException("Monitor executable not found after update.", monitorPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = monitorPath,
            WorkingDirectory = Path.GetDirectoryName(monitorPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false
        };
        startInfo.Environment["DEVICEMON_SUPPRESS_WATCHDOG_UAC"] = "1";
        Process.Start(startInfo);
        log("Monitor restarted.");
    }

    private static void RunProcess(string fileName, string arguments, TimeSpan timeout, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process == null) return;
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                log($"{fileName} timed out.");
                return;
            }

            if (process.ExitCode != 0)
                log($"{fileName} exited with {process.ExitCode}: {process.StandardError.ReadToEnd()}");
        }
        catch (Exception ex)
        {
            log($"{fileName} failed: {ex.Message}");
        }
    }
}

internal sealed record UpdateStatus(
    string Status,
    string Source,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Message,
    string LogPath);

internal sealed record UpdateOptions(
    string Source,
    string TargetDirectory,
    string MonitorPath,
    int? MonitorPid,
    bool Restart,
    string? Username,
    string? Password,
    string? ExpectedSha256,
    string? RequestFile)
{
    public static UpdateOptions? Parse(string[] args)
    {
        var requestFile = GetArg(args, "--request");
        if (!string.IsNullOrWhiteSpace(requestFile))
            return ParseRequestFile(requestFile);

        string? source = null;
        string? target = null;
        string? monitor = null;
        int? pid = null;
        var restart = false;
        string? sha256 = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--source", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                source = args[++i];
            else if (args[i].Equals("--target", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                target = args[++i];
            else if (args[i].Equals("--monitor", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                monitor = args[++i];
            else if (args[i].Equals("--pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out var parsedPid))
                pid = parsedPid;
            else if (args[i].Equals("--restart", StringComparison.OrdinalIgnoreCase))
                restart = true;
            else if (args[i].Equals("--sha256", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                sha256 = args[++i];
        }

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return null;

        target = Path.GetFullPath(Environment.ExpandEnvironmentVariables(target));
        monitor = string.IsNullOrWhiteSpace(monitor)
            ? Path.Combine(target, "DeviceMon.exe")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(monitor));

        return new UpdateOptions(source, target, monitor, pid, restart, null, null, sha256, null);
    }

    public void DeleteRequestFile()
    {
        if (string.IsNullOrWhiteSpace(RequestFile))
            return;

        try { File.Delete(RequestFile); } catch { }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static UpdateOptions? ParseRequestFile(string requestFile)
    {
        var json = File.ReadAllText(requestFile);
        var request = JsonSerializer.Deserialize<UpdateRequest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (request == null || string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.TargetDirectory))
            return null;

        var target = Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.TargetDirectory));
        var monitor = string.IsNullOrWhiteSpace(request.MonitorPath)
            ? Path.Combine(target, "DeviceMon.exe")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.MonitorPath));

        return new UpdateOptions(
            request.Source,
            target,
            monitor,
            request.MonitorPid,
            request.Restart,
            request.Username,
            request.Password,
            request.Sha256,
            requestFile);
    }
}

internal sealed class UpdateRequest
{
    public string Source { get; set; } = "";
    public string TargetDirectory { get; set; } = "";
    public string MonitorPath { get; set; } = "";
    public int? MonitorPid { get; set; }
    public bool Restart { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Sha256 { get; set; }
}

internal static class NativeMethods
{
    internal const int RESOURCETYPE_DISK = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    internal static extern int WNetAddConnection2(ref NETRESOURCE lpNetResource, string? lpPassword, string? lpUsername, int dwFlags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    internal static extern int WNetCancelConnection2(string lpName, int dwFlags, bool fForce);
}
