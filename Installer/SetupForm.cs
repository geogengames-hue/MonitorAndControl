using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;

namespace MonitorAndControl.Installer;

/// <summary>
/// The setup window: status line, progress bar and a live log. Setup itself is
/// self-contained (it runs on a PC with no .NET). DeviceMon needs the .NET 8 Desktop
/// and ASP.NET Core runtimes, so any that are missing are downloaded from Microsoft,
/// signature-checked and installed first.
/// </summary>
internal sealed class SetupForm : Form
{
    private static readonly (string Framework, string Name, string Url)[] RequiredRuntimes =
    {
        ("Microsoft.WindowsDesktop.App", ".NET 8 Desktop Runtime", "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"),
        ("Microsoft.AspNetCore.App", "ASP.NET Core 8 Runtime", "https://aka.ms/dotnet/8.0/aspnetcore-runtime-win-x64.exe"),
    };

    private static readonly TimeSpan RuntimeInstallTimeout = TimeSpan.FromMinutes(20);

    private readonly List<string> _scriptArgs = new(); // arguments for install.ps1 (everything except -AppSettings, -Quiet, -Language)
    private readonly AppSettingsAction? _forcedAppSettings;
    private readonly bool _unattended; // -Quiet or -AppSettings given: never wait for an answer, close when done
    private readonly string? _badAppSettings;
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly TextBox _log;
    private readonly Button _close;
    private bool _busy = true;

    public int ExitCode { get; private set; } = 1;

    public SetupForm(string[] args)
    {
        bool quiet = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("-Quiet", StringComparison.OrdinalIgnoreCase))
            {
                quiet = true;
                continue;
            }
            if (args[i].Equals("-AppSettings", StringComparison.OrdinalIgnoreCase))
            {
                string value = i + 1 < args.Length ? args[++i] : "";
                if (Enum.TryParse(value, ignoreCase: true, out AppSettingsAction action)) _forcedAppSettings = action;
                else _badAppSettings = value;
                continue;
            }
            if (args[i].Equals("-Language", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-Lang", StringComparison.OrdinalIgnoreCase))
            {
                string value = i + 1 < args.Length ? args[++i] : "";
                InstallerLocalization.Language = value;
                continue;
            }
            _scriptArgs.Add(args[i]);
        }
        _unattended = quiet || _forcedAppSettings != null;

        Text = InstallerLocalization.SetupTitle(SetupVersion);
        ClientSize = new Size(640, 380);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { /* keep default icon */ }

        var title = new Label { Text = InstallerLocalization.SetupTitle(SetupVersion), UseMnemonic = false, Font = new Font(Font.FontFamily, 14f, FontStyle.Bold), Location = new Point(60, 16), AutoSize = true };
        var logo = new PictureBox { Location = new Point(16, 8), Size = new Size(36, 36), SizeMode = PictureBoxSizeMode.Zoom };
        using (Stream? logoStream = typeof(SetupForm).Assembly.GetManifestResourceStream("logo.png"))
        {
            if (logoStream != null)
                using (var raw = new Bitmap(logoStream)) logo.Image = new Bitmap(raw);
        }
        _status = new Label { Text = InstallerLocalization.Starting, UseMnemonic = false, Location = new Point(16, 48), Size = new Size(608, 22) };
        _progress = new ProgressBar { Location = new Point(16, 74), Size = new Size(608, 22), Style = ProgressBarStyle.Marquee };
        _log = new TextBox
        {
            Location = new Point(16, 108),
            Size = new Size(608, 222),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f),
            BackColor = Color.White,
        };
        _close = new Button { Text = InstallerLocalization.ButtonClose, Location = new Point(544, 340), Size = new Size(80, 28), Enabled = false };
        _close.Click += (_, _) => Close();
        Controls.AddRange(new Control[] { logo, title, _status, _progress, _log, _close });

        Shown += async (_, _) => await RunAsync();
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    private async Task RunAsync()
    {
        string? workDir = null;
        try
        {
            if (_badAppSettings != null)
                throw new InvalidOperationException(InstallerLocalization.InvalidAppSettings(_badAppSettings));

            AppendLog(InstallerLocalization.SetupTitle(SetupVersion));
            AppendLog(InstallerLocalization.RunningAsAdmin($"{Environment.UserDomainName}\\{Environment.UserName}"));
            workDir = Path.Combine(Path.GetTempPath(), "MonitorAndControlSetup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            foreach (var (framework, name, url) in RequiredRuntimes)
            {
                if (!HasRuntime(framework)) await InstallRuntimeAsync(name, url, workDir);
            }

            SetStatus(InstallerLocalization.ExtractingFiles);
            await Task.Run(() => Extract(workDir));

            PrepareInstall(workDir);

            SetStatus(InstallerLocalization.InstallingMonitorAndControl);
            int code = await RunInstallScriptAsync(workDir);
            if (code != 0) throw new InvalidOperationException(InstallerLocalization.InstallScriptFailed(code));

            ExitCode = 0;
            string installed = Pretty(FileVersion(ResolveInstallDir()));
            Determinate(100);
            SetStatus(InstallerLocalization.InstalledSuccess(installed));
            AppendLog(InstallerLocalization.InstalledVersionLog(installed));
            AppendLog(InstallerLocalization.SetupFinished);
        }
        catch (Exception ex)
        {
            Determinate(0);
            SetStatus(InstallerLocalization.SetupFailed);
            AppendLog(ex.Message);
        }
        finally
        {
            if (workDir != null)
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* temp folder; best effort */ }
            }
            _busy = false;
            _close.Enabled = true;
            // Unattended use (-Quiet or -AppSettings): close on success so scripts are not left waiting.
            if (ExitCode == 0 && _unattended) Close();
        }
    }

    /// <summary>
    /// Reports an existing installation and prepares appsettings.json (keep the user's file, import it
    /// from an older install elsewhere, ask what to do with options that are new in this version).
    /// </summary>
    private void PrepareInstall(string workDir)
    {
        string installDir = ResolveInstallDir();
        string? previousDir = FindServiceDirectory();
        string? newVersion = FileVersion(workDir);
        AppendLog(InstallerLocalization.VersionToInstall(Pretty(newVersion)));

        string? foundDir = FileVersion(installDir) != null ? installDir : previousDir;
        if (foundDir == null)
        {
            AppendLog(InstallerLocalization.VersionOnDiskNone);
        }
        else
        {
            string? foundVersion = FileVersion(foundDir);
            AppendLog(InstallerLocalization.VersionOnDiskFound(Pretty(foundVersion), foundDir));
            if (Version.TryParse(foundVersion, out Version? found) && Version.TryParse(newVersion, out Version? bundled))
            {
                int order = found.CompareTo(bundled);
                AppendLog(order == 0 ? InstallerLocalization.SameVersionReinstall
                    : order < 0 ? InstallerLocalization.UpgradingVersion(Pretty(foundVersion), Pretty(newVersion))
                    : InstallerLocalization.DowngradingVersion(Pretty(foundVersion), Pretty(newVersion)));
            }
        }

        // Unattended runs (-Quiet or -AppSettings) never wait for an answer: -AppSettings, else Merge.
        AppSettingsAction Choose(IReadOnlyList<(string Name, string Value)> newOptions)
        {
            if (_forcedAppSettings is { } forced) return forced;
            if (_unattended) return AppSettingsAction.Merge;
            using var dialog = new AppSettingsDialog(newOptions);
            dialog.ShowDialog(this);
            return dialog.Choice;
        }

        AppSettingsMigration.Prepare(installDir, previousDir, Path.Combine(workDir, "appsettings.json"), Choose, AppendLog);

        if (previousDir != null && !string.Equals(Path.GetFullPath(previousDir).TrimEnd('\\'), Path.GetFullPath(installDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            AppendLog(InstallerLocalization.PreviousInstallReplaced(previousDir));
    }

    private string ResolveInstallDir()
    {
        for (int i = 0; i + 1 < _scriptArgs.Count; i++)
        {
            if (_scriptArgs[i].Equals("-InstallDir", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(_scriptArgs[i + 1].Trim('"'));
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DeviceMon");
    }

    /// <summary>Folder of the installed GameHost service (or its older names), if one is registered.</summary>
    private static string? FindServiceDirectory()
    {
        foreach (string name in new[] { "GameHost", "MonitorAndControlWatchdog", "SystemHelperWatchdog" })
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            if (key?.GetValue("ImagePath") is not string image) continue;
            string exe = Environment.ExpandEnvironmentVariables(image.Trim());
            if (exe.StartsWith('"'))
            {
                int close = exe.IndexOf('"', 1);
                exe = close > 1 ? exe[1..close] : exe.Trim('"');
            }
            else
            {
                int ext = exe.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (ext >= 0) exe = exe[..(ext + 4)];
            }
            string? dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
        }
        return null;
    }

    private static string SetupVersion =>
        "v" + Pretty(typeof(SetupForm).Assembly.GetName().Version?.ToString());

    /// <summary>"1.13.0.0" -> "1.13.0".</summary>
    private static string Pretty(string? version) =>
        Version.TryParse(version, out Version? v) ? (v.Revision <= 0 && v.Build >= 0 ? v.ToString(3) : v.ToString()) : version ?? "unknown";

    private static string? FileVersion(string dir)
    {
        foreach (string exe in new[] { "DeviceMon.exe", "MonitorAndControl.exe", "SystemHelper.exe" })
        {
            string path = Path.Combine(dir, exe);
            if (File.Exists(path)) return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        return null;
    }

    private static bool HasRuntime(string framework)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", framework);
        return Directory.Exists(root) && Directory.EnumerateDirectories(root, "8.*").Any();
    }

    private async Task InstallRuntimeAsync(string name, string url, string workDir)
    {
        string file = Path.Combine(workDir, Path.GetFileName(new Uri(url).AbsolutePath));
        string logFile = Path.Combine(Path.GetTempPath(), "MonitorAndControlSetup-runtime.log");
        try
        {
            AppendLog(InstallerLocalization.RuntimeNotInstalled(name));
            await DownloadAsync(name, url, file);

            // Only run the download if Windows confirms it is validly signed by Microsoft.
            SetStatus(InstallerLocalization.VerifyingRuntime(name));
            Indeterminate();
            int signature = await Task.Run(() => RunPowerShell(
                "$s = Get-AuthenticodeSignature -LiteralPath $env:DM_FILE; " +
                "if ($s.Status -eq 'Valid' -and $s.SignerCertificate.Subject -match 'O=Microsoft Corporation') { exit 0 } else { exit 1 }", file));
            if (signature != 0) throw new InvalidOperationException(InstallerLocalization.DownloadNotSigned);

            // /passive shows Microsoft's own progress window, so a slow install is visible.
            AppendLog(InstallerLocalization.InstallingRuntimePrompt(name));
            var psi = new ProcessStartInfo(file) { UseShellExecute = false };
            psi.ArgumentList.Add("/install");
            psi.ArgumentList.Add("/passive");
            psi.ArgumentList.Add("/norestart");
            psi.ArgumentList.Add("/log");
            psi.ArgumentList.Add(logFile);
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException(InstallerLocalization.CouldNotStartRuntimeInstaller);
            var clock = Stopwatch.StartNew();
            while (!p.HasExited)
            {
                if (clock.Elapsed > RuntimeInstallTimeout)
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    throw new InvalidOperationException(InstallerLocalization.RuntimeInstallerTimeout);
                }
                SetStatus(InstallerLocalization.InstallingRuntimeProgress(name, $"{clock.Elapsed:m\\:ss}"));
                await Task.Delay(500);
            }
            // 3010 = installed, restart pending; 1638 = a newer version is already present.
            if (p.ExitCode is not (0 or 3010 or 1638)) throw new InvalidOperationException(InstallerLocalization.RuntimeInstallerExitCode(p.ExitCode));
            AppendLog(InstallerLocalization.RuntimeInstalled(name));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                InstallerLocalization.RuntimeInstallFailed(name, ex.Message, logFile));
        }
    }

    private async Task DownloadAsync(string name, string url, string file)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(file);

        if (total <= 0) { Indeterminate(); SetStatus(InstallerLocalization.DownloadingRuntime(name)); }
        var buffer = new byte[81920];
        long done = 0;
        int lastPercent = -1, read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            if (total <= 0) continue;
            int percent = (int)(done * 100 / total);
            if (percent == lastPercent) continue;
            lastPercent = percent;
            Determinate(percent);
            SetStatus(InstallerLocalization.DownloadingRuntimeProgress(name, done / 1048576.0, total / 1048576.0));
        }
    }

    private static void Extract(string workDir)
    {
        using Stream payload = typeof(SetupForm).Assembly.GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException(InstallerLocalization.NoPayload);
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        zip.ExtractToDirectory(workDir);
    }

    private async Task<int> RunInstallScriptAsync(string workDir)
    {
        string script = Path.Combine(workDir, "install.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException(InstallerLocalization.InstallScriptMissing, script);

        Indeterminate();
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-SourceDir", workDir }) psi.ArgumentList.Add(a);
        foreach (string arg in _scriptArgs) psi.ArgumentList.Add(arg);

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppendLog(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppendLog(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    private static string PowerShellPath() =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static int RunPowerShell(string command, string file)
    {
        var psi = new ProcessStartInfo { FileName = PowerShellPath(), UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        psi.Environment["DM_FILE"] = file;
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException(InstallerLocalization.CouldNotStartPowerShell);
        p.WaitForExit();
        return p.ExitCode;
    }

    private void SetStatus(string text) => OnUi(() => _status.Text = text);

    private void Indeterminate() => OnUi(() => _progress.Style = ProgressBarStyle.Marquee);

    private void Determinate(int percent) => OnUi(() =>
    {
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = Math.Clamp(percent, 0, 100);
    });

    private void AppendLog(string line) => OnUi(() => _log.AppendText(line + Environment.NewLine));

    private void OnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}
