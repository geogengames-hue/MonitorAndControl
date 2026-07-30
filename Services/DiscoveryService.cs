using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

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
        ScanLooseGameFolders(results);

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
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch { }
    }

    private void ScanCommonGameDirs(Dictionary<string, DiscoveredApp> results)
    {
        var dirs = new List<string>
        {
            @"C:\Program Files\Epic Games",
            @"C:\Program Files (x86)\Epic Games",
            @"C:\Program Files\Origin Games",
            @"C:\Program Files (x86)\Origin Games",
            @"C:\Program Files\EA Games",
            @"C:\Program Files (x86)\EA Games",
            @"C:\Program Files\Ubisoft\Ubisoft Game Launcher\games",
            @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games",
            @"C:\Program Files\Battle.net\Games",
            @"C:\Program Files (x86)\Battle.net\Games",
            @"C:\Program Files\GOG Galaxy\Games",
            @"C:\Program Files (x86)\GOG Galaxy\Games",
            @"C:\Program Files\WindowsApps",
            @"C:\Program Files\ModifiableWindowsApps"
        };

        // Steam games can live on any drive - resolve every configured Steam
        // library from libraryfolders.vdf, not just the default C:\ install.
        dirs.AddRange(GetSteamLibraryCommonDirs());

        foreach (var dir in dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            ScanGameContainerDir(results, dir, "Game Directory");
    }

    /// <summary>
    /// Scans folders where manually-installed or repacked/"cracked" games commonly
    /// land: the user's Downloads folder and any top-level "Games" folder on each
    /// fixed drive. Each immediate subfolder is treated as a candidate game.
    /// </summary>
    private void ScanLooseGameFolders(Dictionary<string, DiscoveredApp> results)
    {
        var roots = new List<string>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            roots.Add(Path.Combine(userProfile, "Downloads"));
            roots.Add(Path.Combine(userProfile, "Games"));
        }

        var publicDir = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrEmpty(publicDir))
            roots.Add(Path.Combine(publicDir, "Games"));

        try
        {
            foreach (var drive in DriveInfo.GetDrives()
                         .Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Games"));
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Downloads"));
            }
        }
        catch { }

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            ScanGameContainerDir(results, root, "Games Folder");
    }

    /// <summary>
    /// Treats each immediate subfolder of <paramref name="containerDir"/> as a game
    /// and adds up to a few likely game executables found within it.
    /// </summary>
    private void ScanGameContainerDir(Dictionary<string, DiscoveredApp> results, string containerDir, string source)
    {
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(containerDir))
            {
                try
                {
                    var dirName = Path.GetFileName(subDir);
                    var candidates = Directory.EnumerateFiles(subDir, "*.exe", SearchOption.AllDirectories)
                        .Where(IsLikelyGameExe)
                        .Take(40)
                        .ToList();
                    if (candidates.Count == 0) continue;

                    // A folder is one game: report only its main executable (the one
                    // whose name matches the folder, else the largest binary) instead
                    // of every helper/tool exe, which otherwise floods Downloads.
                    var exe = PickMainExe(candidates, dirName);
                    var procName = ExtractProcessName(exe);
                    if (string.IsNullOrEmpty(procName)) continue;
                    if (results.ContainsKey(procName)) continue;
                    if (IsSystemApp(procName, exe)) continue;

                    var displayName = FriendlyNameFromExe(procName);
                    if (!string.IsNullOrEmpty(dirName) && dirName.Length > 2 && dirName.Length < 50)
                        displayName = dirName;

                    results[procName] = new DiscoveredApp(procName, displayName, exe, source);
                }
                catch { }
            }
        }
        catch { }
    }

    private static string PickMainExe(List<string> exes, string dirName)
    {
        if (exes.Count == 1) return exes[0];

        static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var dn = Norm(dirName);

        // Prefer an executable whose name matches (or is contained in) the folder name.
        if (!string.IsNullOrEmpty(dn))
        {
            var match = exes.FirstOrDefault(e =>
            {
                var n = Norm(Path.GetFileNameWithoutExtension(e));
                return n.Length > 2 && (dn.Contains(n) || n.Contains(dn));
            });
            if (match != null) return match;
        }

        // Otherwise the largest executable - main game binaries dwarf helpers.
        return exes
            .OrderByDescending(e => { try { return new FileInfo(e).Length; } catch { return 0L; } })
            .First();
    }

    private static bool IsLikelyGameExe(string path)
    {
        var lower = path.ToLowerInvariant();
        return !lower.Contains("_commonredist") &&
               !lower.Contains("\\redist\\") &&
               !lower.Contains("vc_redist") &&
               !lower.Contains("dxwebsetup") &&
               !lower.Contains("dxsetup") &&
               !lower.Contains("\\eossdk\\") &&
               !lower.Contains("\\epic\\") &&
               !lower.Contains("\\easyanticheat\\") &&
               !lower.Contains("\\battleye\\") &&
               !lower.Contains("\\support\\") &&
               !lower.Contains("\\pdb\\") &&
               !lower.Contains("\\crash\\") &&
               !lower.Contains("\\dotnet\\") &&
               !lower.Contains("\\vs_") &&
               !lower.Contains("\\_installer\\") &&
               !lower.Contains("\\python_embeded\\") &&
               !lower.Contains("\\python\\") &&
               !lower.Contains("\\scripts\\") &&
               !lower.Contains("\\site-packages\\") &&
               !lower.Contains("\\node_modules\\") &&
               !lower.Contains("\\__pycache__\\") &&
               !lower.Contains("unins") &&
               !lower.EndsWith("\\setup.exe");
    }

    /// <summary>
    /// Returns the <c>steamapps\common</c> folder of every configured Steam library
    /// (across all drives), resolved from Steam's <c>libraryfolders.vdf</c>.
    /// </summary>
    private static IEnumerable<string> GetSteamLibraryCommonDirs()
    {
        var steamPaths = new List<string>();
        try
        {
            if (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") is string p1 && !string.IsNullOrWhiteSpace(p1))
                steamPaths.Add(p1);
        }
        catch { }
        try
        {
            if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") is string p2 && !string.IsNullOrWhiteSpace(p2))
                steamPaths.Add(p2);
        }
        catch { }
        steamPaths.Add(@"C:\Program Files (x86)\Steam");
        steamPaths.Add(@"C:\Program Files\Steam");

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steam in steamPaths.Select(s => s.Replace('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            libraries.Add(steam); // The Steam install folder is itself a library.
            foreach (var vdf in new[]
                     {
                         Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                         Path.Combine(steam, "config", "libraryfolders.vdf")
                     })
            {
                try
                {
                    if (!File.Exists(vdf)) continue;
                    foreach (var libPath in ParseSteamLibraryPaths(File.ReadAllText(vdf)))
                        libraries.Add(libPath);
                }
                catch { }
            }
        }

        var commonDirs = new List<string>();
        foreach (var lib in libraries)
        {
            try
            {
                var common = Path.Combine(lib, "steamapps", "common");
                if (Directory.Exists(common))
                    commonDirs.Add(common);
            }
            catch { }
        }
        return commonDirs;
    }

    /// <summary>
    /// Parses the <c>"path"  "…"</c> entries out of a Steam <c>libraryfolders.vdf</c>
    /// file, unescaping the doubled backslashes Steam writes.
    /// </summary>
    public static IEnumerable<string> ParseSteamLibraryPaths(string vdfContent)
    {
        if (string.IsNullOrEmpty(vdfContent))
            yield break;

        foreach (Match m in Regex.Matches(vdfContent, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var raw = m.Groups[1].Value.Replace(@"\\", @"\").Trim();
            if (!string.IsNullOrWhiteSpace(raw))
                yield return raw;
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
