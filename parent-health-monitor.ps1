# Run on the parent PC, not the child PC.
# Example:
# .\parent-health-monitor.ps1 -ChildUrl "http://child-pc:5000" -GmailAddress "parent@gmail.com" -AppPassword "xxxx xxxx xxxx xxxx"
param(
    [Parameter(Mandatory = $true)]
    [string]$ChildUrl,

    [Parameter(Mandatory = $true)]
    [string]$GmailAddress,

    [Parameter(Mandatory = $true)]
    [string]$AppPassword,

    [string]$To = $GmailAddress,
    [int]$CheckSeconds = 60,
    [int]$MissesBeforeAlert = 3
)

$ErrorActionPreference = 'Stop'
$healthUrl = ($ChildUrl.TrimEnd('/')) + '/api/health'
$misses = 0
$wasDown = $false

function Send-MonitorMail {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Subject,

        [Parameter(Mandatory = $true)]
        [string]$Body
    )

    $message = [System.Net.Mail.MailMessage]::new()
    $message.From = $GmailAddress
    $message.To.Add($To)
    $message.Subject = $Subject
    $message.Body = $Body

    $client = [System.Net.Mail.SmtpClient]::new('smtp.gmail.com', 587)
    $client.EnableSsl = $true
    $client.Credentials = [System.Net.NetworkCredential]::new($GmailAddress, ($AppPassword -replace '\s+', ''))

    try {
        $client.Send($message)
    }
    finally {
        $message.Dispose()
        $client.Dispose()
    }
}

Write-Host "Monitoring $healthUrl every $CheckSeconds seconds. Alerts after $MissesBeforeAlert missed checks."

while ($true) {
    try {
        $response = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 10
        if ($response.status -eq 'ok') {
            if ($wasDown) {
                Send-MonitorMail `
                    -Subject "Monitor recovered: $ChildUrl" `
                    -Body "MonitorAndControl is responding again at $healthUrl.`nTime: $(Get-Date)"
                $wasDown = $false
            }
            $misses = 0
            Write-Host "$(Get-Date -Format s) OK $($response.machine)"
        }
        else {
            throw "Unexpected health response"
        }
    }
    catch {
        $misses++
        Write-Host "$(Get-Date -Format s) MISS $misses/$MissesBeforeAlert - $($_.Exception.Message)"

        if (-not $wasDown -and $misses -ge $MissesBeforeAlert) {
            Send-MonitorMail `
                -Subject "Monitor offline: $ChildUrl" `
                -Body "MonitorAndControl is not responding at $healthUrl after $misses checks.`nTime: $(Get-Date)`nError: $($_.Exception.Message)"
            $wasDown = $true
        }
    }

    Start-Sleep -Seconds $CheckSeconds
}
