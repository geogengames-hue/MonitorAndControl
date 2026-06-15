# ParentNotifier.ps1
# Run this on YOUR PC to receive popup notifications when limits are hit.
# Requires PowerShell 5.1+ and .NET 8 Runtime.
#
# Usage: .\ParentNotifier.ps1
# Then set webhook URL in dashboard to: http://YOUR_PC_IP:9999/hook?token=YOUR_SECRET

param(
    [int]$Port = 9999,
    [string]$Secret = ""
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Web

$isRunning = $true

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://+:$Port/")
$listener.Start()

Write-Host "Parent Notifier listening on port $Port"
Write-Host "Set webhook URL in dashboard to: http://$((Get-NetIPAddress -AddressFamily IPv4 | Where-Object {$_.InterfaceAlias -ne 'Loopback' -and $_.PrefixOrigin -ne 'LinkLocal'}).IPAddress[0]):$Port/hook$(if ($Secret) { "?token=$Secret" })"
Write-Host "Press Ctrl+C to stop.`n"

while ($isRunning) {
    try {
        $context = $listener.GetContext()
        $request = $context.Request
        $response = $context.Response

        if ($request.Url.LocalPath -eq '/hook' -and $request.HttpMethod -eq 'POST') {
            if ($Secret -and $request.QueryString['token'] -ne $Secret) {
                $response.StatusCode = 401
                $response.Close()
                continue
            }

            $reader = New-Object System.IO.StreamReader($request.InputStream)
            $json = $reader.ReadToEnd()
            $reader.Close()

            $data = $json | ConvertFrom-Json

            # Show Windows notification
            $notification = New-Object System.Windows.Forms.NotifyIcon
            $notification.Icon = [System.Drawing.SystemIcons]::Warning
            $notification.Visible = $true
            $title = ($data.type -replace '_',' ')
            $notification.BalloonTipTitle = (Get-Culture).TextInfo.ToTitleCase($title)
            $notification.BalloonTipText = $data.message
            $notification.BalloonTipIcon = [System.Windows.Forms.ToolTipIcon]::Warning
            $notification.ShowBalloonTip(10000)

            # Also write to console
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $($data.type): $($data.message)"

            # Auto-dispose after balloon
            Start-Sleep -Seconds 12
            $notification.Dispose()

            $response.StatusCode = 200
        } else {
            # Health check or other paths
            $response.StatusCode = 200
            $buffer = [System.Text.Encoding]::UTF8.GetBytes('{"status":"ok"}')
            $response.OutputStream.Write($buffer, 0, $buffer.Length)
        }

        $response.Close()
    } catch {
        if ($_.Exception.Message -notmatch 'aborted|closed') {
            Write-Host "Error: $($_.Exception.Message)"
        }
    }
}

$listener.Stop()
