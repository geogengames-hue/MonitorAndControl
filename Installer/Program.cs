using System.Diagnostics;
using System.Security.Principal;

namespace MonitorAndControl.Installer;

/// <summary>
/// Single-file installer. The whole publish folder (DeviceMon.exe, GameHost.exe,
/// PopupHost.exe, UpdateAgent.exe, wwwroot, appsettings.json, install.ps1 ...) is
/// embedded as "payload.zip". Setup shows a progress window, installs any missing
/// .NET 8 runtime, extracts the payload to a temporary folder and runs the bundled
/// install.ps1, so the install logic lives in one place.
///
/// Any command-line arguments are passed on to install.ps1, e.g.
///   MonitorAndControlSetup.exe -InstallDir "D:\DeviceMon" -UpdateSource "https://.../DeviceMon.zip"
/// The manifest requests administrator rights, so Windows shows one UAC prompt. As a
/// second line of defence, Setup never installs without elevation: if it is somehow
/// started un-elevated it relaunches itself elevated, or stops. (The GameHost service
/// cannot be installed with lower rights.)
/// </summary>
internal static class Program
{
    private const int ExitNotElevated = 5; // ERROR_ACCESS_DENIED

    [STAThread]
    private static int Main(string[] args)
    {
        if (!IsElevated()) return RelaunchElevated(args);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        var form = new SetupForm(args);
        Application.Run(form);
        return form.ExitCode;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int RelaunchElevated(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("unknown path"))
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("could not start");
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception)
        {
            MessageBox.Show(
                "Monitor & Control Setup needs administrator rights to install the GameHost service.\n\nRun it again and choose Yes at the Windows prompt.",
                "Monitor & Control Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return ExitNotElevated;
        }
    }
}
