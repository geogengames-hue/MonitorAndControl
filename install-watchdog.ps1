# Run as Administrator from the publish folder or pass -PublishDir.
param(
    [string]$PublishDir = (Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\publish'),
    [string]$ServiceName = 'GameHost',
    [int]$IntervalSeconds = 15
)

$publishPath = Resolve-Path -LiteralPath $PublishDir -ErrorAction Stop
$watchdogExe = Join-Path $publishPath 'GameHost.exe'
$monitorExe = Join-Path $publishPath 'DeviceMon.exe'

if (-not (Test-Path -LiteralPath $watchdogExe)) {
    throw "Watchdog executable not found: $watchdogExe. Run publish.ps1 first."
}
if (-not (Test-Path -LiteralPath $monitorExe)) {
    throw "Monitor executable not found: $monitorExe. Run publish.ps1 first."
}

$binPath = "`"$watchdogExe`" --monitor `"$monitorExe`" --interval $IntervalSeconds"

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}
foreach ($legacyName in @('MonitorAndControlWatchdog', 'SystemHelperWatchdog')) {
    $legacy = Get-Service -Name $legacyName -ErrorAction SilentlyContinue
    if ($legacy) {
        Stop-Service -Name $legacyName -Force -ErrorAction SilentlyContinue
        sc.exe delete $legacyName | Out-Null
        Start-Sleep -Seconds 2
    }
}

sc.exe create $ServiceName binPath= $binPath start= auto obj= LocalSystem DisplayName= "GameHost" | Out-Null
sc.exe description $ServiceName "Restarts DeviceMon.exe if it is stopped." | Out-Null
Start-Service -Name $ServiceName

Write-Host "Installed and started $ServiceName."
Write-Host "Monitor: $monitorExe"
Write-Host "Log: C:\ProgramData\SystemHelper\watchdog.log"
