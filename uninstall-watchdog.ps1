# Run as Administrator.
param(
    [string]$ServiceName = 'GameHost'
)

foreach ($name in @($ServiceName, 'MonitorAndControlWatchdog', 'SystemHelperWatchdog')) {
    $existing = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $existing) {
        Write-Host "$name is not installed."
        continue
    }

    Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
    sc.exe delete $name | Out-Null
    Write-Host "Removed $name."
}
