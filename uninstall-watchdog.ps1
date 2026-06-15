# Run as Administrator.
param(
    [string]$ServiceName = 'SystemHelperWatchdog'
)

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "$ServiceName is not installed."
    return
}

Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
sc.exe delete $ServiceName | Out-Null
Write-Host "Removed $ServiceName."
