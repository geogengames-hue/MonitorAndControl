using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using MonitorAndControl.ScreenTime;

namespace MonitorAndControl.Services;

internal static class ScreenTimeClient
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    internal static async Task<ScreenTimeReply> SendAsync(ScreenTimeRequest request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var service = new System.ServiceProcess.ServiceController("GameHost");
        if (service.Status != System.ServiceProcess.ServiceControllerStatus.Running)
            throw new InvalidOperationException("The GameHost service is stopped.");
        using var pipe = new NamedPipeClientStream(".", ScreenTimeProtocol.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token);
        // Never send Windows credentials to a pipe owned by an interactive process.
        if (!GetNamedPipeServerSessionId(pipe.SafePipeHandle, out var session) || session != 0
            || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid))
            throw new UnauthorizedAccessException("Could not verify the local service.");
        using var process = Process.GetProcessById((int)pid);
        if (!process.ProcessName.Equals("GameHost", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Local service not recognized.");
        await ScreenTimeProtocol.WriteAsync(pipe, request, timeout.Token);
        return await ScreenTimeProtocol.ReadAsync<ScreenTimeReply>(pipe, 1024 * 1024, timeout.Token);
    }
}
