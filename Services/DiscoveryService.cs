using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorAndControl.Services;

public class DiscoveryService
{
    public record DiscoveredApp(string ProcessName, string DisplayName, string? InstallPath, string Source);

    public List<DiscoveredApp> ScanForApps()
    {
        var results = new Dictionary<string, DiscoveredApp>(StringComparer.OrdinalIgnoreCase);

        ScanStartMenu(results);
        ScanRunningProcesses(results);
        ScanCommonGameDirs(results);

        return results.Values
            .OrderBy(a => a.DisplayName)
            .ThenBy(a => a.ProcessName)
            .ToList();
    }

    public static string ExtractProcessName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try
        {
            var name = Path.GetFileName(path);
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name += ".exe";
            return name;
        }
        catch { return ""; }
    }

    public static string FriendlyNameFromExe(string exeName)
    {
        return Path.GetFileNameWithoutExtension(exeName)
            .Replace("_", " ")
            .Replace("-", " ")
            .Replace(".", " ")
            .Trim();
    }

    private void ScanStartMenu(Dictionary<string, DiscoveredApp> results)
    {
        var paths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in paths.Where(Directory.Exists))
        {
            try
            {
                var extensions = new[] { "*.lnk", "*.appref-ms" };
                foreach (var pattern in extensions)
                {
                    foreach (var lnk in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
                    {
                        try
                        {
                            var lnkName = Path.GetFileNameWithoutExtension(lnk);
                            if (string.IsNullOrEmpty(lnkName)) continue;
                            if (lnkName.EndsWith(" - Shortcut", StringComparison.OrdinalIgnoreCase))
                                lnkName = lnkName[..^" - Shortcut".Length];
                            if (lnkName.Equals("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;

                            var target = ResolveShortcut(lnk);
                            if (string.IsNullOrEmpty(target)) continue;

                        var procName = ExtractProcessName(target);
                        if (string.IsNullOrEmpty(procName) || procName.Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                        var key = procName.ToLowerInvariant();
                            if (seen.Contains(key)) continue;
                            seen.Add(key);

                            if (IsSystemApp(procName, target)) continue;

                            results[procName] = new DiscoveredApp(procName, lnkName, target, "Start Menu");
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }

    private void ScanRunningProcesses(Dictionary<string, DiscoveredApp> results)
    {
        try
        {
            foreach (var proc in Process.GetProcesses().OrderBy(p => p.ProcessName))
            {
                try
                {
                    var procName = proc.ProcessName + ".exe";
                    if (results.ContainsKey(procName)) continue;
                    if (string.IsNullOrEmpty(proc.MainWindowTitle)) continue;
                    if (IsSystemProcess(proc)) continue;

                    var title = proc.MainWindowTitle;
                    if (title.Length > 40 || title.StartsWith(".") || title.Contains(" - Shortcut"))
                        title = FriendlyNameFromExe(procName);

                    string? path = null;
                    try { path = proc.MainModule?.FileName; } catch { }

                    if (IsSystemApp(procName, path)) continue;

                    results.TryAdd(procName, new DiscoveredApp(procName, title, path, "Running"));
                }
                catch { }
            }
        }
        catch { }
    }

    private void ScanCommonGameDirs(Dictionary<string, DiscoveredApp> results)
    {
        var dirs = new[]
        {
            @"C:\Program Files\Steam\steamapps\common",
            @"C:\Program Files (x86)\Steam\steamapps\common",
            @"C:\Program Files\Epic Games",
            @"C:\Program Files (x86)\Epic Games",
            @"C:\Program Files\Origin Games",
            @"C:\Program Files (x86)\Origin Games",
            @"C:\Program Files\Ubisoft\Ubisoft Game Launcher\games",
            @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games",
            @"C:\Program Files\Battle.net\Games",
            @"C:\Program Files (x86)\Battle.net\Games",
            @"C:\Program Files\WindowsApps",
            @"C:\Program Files\ModifiableWindowsApps"
        };

        foreach (var dir in dirs.Where(Directory.Exists))
        {
            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    try
                    {
                        var exes = Directory.EnumerateFiles(subDir, "*.exe", SearchOption.AllDirectories)
                            .Where(f => {
                                var lower = f.ToLowerInvariant();
                                return !lower.Contains("_commonredist") &&
                                       !lower.Contains("\\redist\\") &&
                                       !lower.Contains("vc_redist") &&
                                       !lower.Contains("dxwebsetup") &&
                                       !lower.Contains("\\eossdk\\") &&
                                       !lower.Contains("\\epic\\") &&
                                       !lower.Contains("\\easyanticheat\\") &&
                                       !lower.Contains("\\battleye\\") &&
                                       !lower.Contains("\\support\\") &&
                                       !lower.Contains("\\pdb\\") &&
                                       !lower.Contains("\\crash\\") &&
                                       !lower.Contains("\\dotnet\\") &&
                                       !lower.Contains("\\vs_") &&
                                       !lower.Contains("\\_installer\\");
                            })
                            .Take(3);

                        foreach (var exe in exes)
                        {
                            var procName = ExtractProcessName(exe);
                            if (string.IsNullOrEmpty(procName)) continue;
                            if (results.ContainsKey(procName)) continue;
                            if (IsSystemApp(procName, exe)) continue;

                            var displayName = FriendlyNameFromExe(procName);
                            var dirName = Path.GetFileName(subDir);
                            if (!string.IsNullOrEmpty(dirName) && dirName.Length > 2 && dirName.Length < 50)
                                displayName = dirName;

                            results[procName] = new DiscoveredApp(procName, displayName, exe, "Game Directory");
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static string? ResolveShortcut(string shortcutPath)
    {
        try
        {
            var shell = Type.GetTypeFromProgID("WScript.Shell");
            if (shell == null) return null;
            var shellObj = Activator.CreateInstance(shell);
            if (shellObj == null) return null;

            var shortcut = shellObj.GetType().InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shellObj,
                new object[] { shortcutPath });
            if (shortcut == null) return null;

            var target = shortcut.GetType().InvokeMember("TargetPath",
                System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;

            if (shortcut is IDisposable d) d.Dispose();
            if (shellObj is IDisposable d2) d2.Dispose();

            return target;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSystemProcess(Process proc)
    {
        try
        {
            var name = proc.ProcessName.ToLowerInvariant();
            return name is "svchost" or "system" or "idle" or "csrss" or "wininit"
                or "services" or "lsass" or "smss" or "conhost" or "rundll32"
                or "sihost" or "taskhostw" or "ctfmon" or "explorer"
                or "shellexperiencehost" or "searchapp" or "runtimebroker"
                or "applicationframehost" or "systemsettings" or "lockapp"
                or "dwm" or "ntoskrnl" or "startmenuexperiencehost";
        }
        catch { return true; }
    }

    private static bool IsSystemApp(string procName, string? path)
    {
        var name = Path.GetFileNameWithoutExtension(procName).ToLowerInvariant();
        var procLower = procName.ToLowerInvariant();

        var noiseExtensions = new[] { ".url.exe", ".html.exe", ".txt.exe",
            ".chm.exe", ".msi.exe", ".bat.exe", ".php.exe" };
        if (noiseExtensions.Any(e => procLower.EndsWith(e))) return true;

        var noisePrefixes = new[] { "ms", "vcruntime", "vc_redist", "dx",
            "xinput", "d3d", "d2d", "dwrite", "unins", "setup", "install",
            "vc_redist", "redist", "nsight" };
        if (noisePrefixes.Any(p => name.StartsWith(p))) return true;

        var noiseContains = new[] { "redist", "install", "setup", "unins",
            "documentation", "sample", "tool", "sdk", "vs_" };
        if (noiseContains.Any(p => name.Contains(p))) return true;

        if (path != null)
        {
            var lower = path.ToLowerInvariant();
            if (lower.Contains("system32") || lower.Contains("syswow64") ||
                lower.Contains("microsoft.net") || lower.Contains("windows\\winsxs"))
                return true;
        }

        var noiseExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "notepad.exe", "calc.exe", "mspaint.exe", "cmd.exe",
            "powershell.exe", "regedit.exe", "taskmgr.exe",
            "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe",
            "code.exe", "devenv.exe", "chrome.exe", "msedge.exe",
            "firefox.exe", "explorer.exe", "wmplayer.exe", "brave.exe",
            "cursor.exe", "openCode.exe", "codex.exe", "telegram.exe",
            "discord.exe", "spotify.exe", "slack.exe", "teams.exe",
            "zoom.exe", "obs64.exe", "obs32.exe", "vlc.exe",
            "audacity.exe", "gimp-3.exe", "handbrake.exe", "7zfm.exe",
            "notepad++.exe", "git-bash.exe", "git-cmd.exe", "git-gui.exe",
            "python.exe", "pythonw.exe", "node.exe",
            "windowsTerminal.exe", "wt.exe", "terminal.exe",
            "acrobat.exe", "acrord32.exe", "acrord64.exe",
            "soffice.exe", "sbase.exe", "scalc.exe", "sdraw.exe",
            "simpress.exe", "smath.exe", "swriter.exe",
            "onenote.exe", "setlang.exe", "oneDrive.exe",
            "teamviewer.exe", "tvnserver.exe", "tvnviewer.exe",
            "logioptionsplus.exe", "gpuz.exe", "GPUView.exe",
            "wpa.exe", "wprui.exe", "appcertui.exe",
            "stackbuilder.exe", "presentationmanager.exe",
            "treeSizeFree.exe", "VMCreate.exe",
            "IntelGraphicsSoftware.exe", "NVIDIA App.exe",
            "Nsight.Monitor.exe", "ncu-ui.exe", "nsys-ui.exe",
            "malwarebytes.exe", "pgadmin4.exe",
            "Hide.me.exe", "LM Studio.exe", "comet.exe",
            "GitHubDesktop.exe", "githubdesktop.exe",
            "studio64.exe", "blend.exe", "camtasiastudio.exe",
            "camtasiaicons.exe", "ts3client_win64.exe",
            "userbenchmark.exe", "widget.exe",
            "MuMuNxMain.exe", "textinputhost.exe",
            "ghelper.exe", "launcherpatcher.exe",
            "crashreportclient.exe", "epicwebhelper.exe",
            "redengineerrorreporter.exe", "redprelauncher.exe",
            "epiconlineservicesuihelper.exe", "epiconlineservicesuserhelper.exe",
            "private_browsing.exe", "blackmagicrawplayer.exe",
            "blackmagicrawspeedtest.exe", "resolve.exe",
            "blackmagicproxygeneratorlite.exe"
        };
        if (noiseExes.Contains(procName))
            return true;

        if (path != null)
        {
            var lower = path.ToLowerInvariant();
            if (lower.Contains("\\edge\\") || lower.Contains("\\chrome\\") ||
                lower.Contains("\\firefox\\") || lower.Contains("\\brave\\"))
                return true;
        }

        return false;
    }
}
