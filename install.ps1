# Installs DeviceMon into a protected Program Files location so a standard (child)
# user cannot delete or modify the application files, then installs the GameHost
# watchdog service pointing at that location.
#
# Run as Administrator.  Re-run this script (as Administrator) with a newer publish
# folder to UPDATE an existing installation: it stops the watchdog, copies the new
# files over, and restarts everything. The GameHost service then relaunches
# DeviceMon.exe inside the child's session.

param(
    # Auto-detect the published files: alongside this script (the usual case when run
    # from an extracted DeviceMon.zip), else the repo's publish output.
    [string]$SourceDir = $(
        if (Test-Path (Join-Path $PSScriptRoot 'DeviceMon.exe')) { $PSScriptRoot }
        elseif (Test-Path (Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\publish\DeviceMon.exe')) { Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\publish' }
        else { $PSScriptRoot }
    ),
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'DeviceMon'),
    [string]$ServiceName = 'GameHost',
    [int]$IntervalSeconds = 5,

    # Optional: configure quiet, admin-level updates for this protected install.
    # $UpdateSource may be an HTTPS .zip URL, a local folder, or a UNC share.
    # Stored in a SYSTEM-only file so the child cannot read or redirect it.
    #
    # For remote silent upgrades to NEW versions, point $UpdateSource at a STABLE
    # "latest" URL (e.g. a GitHub releases/latest/download/<name>.zip asset) and
    # leave $UpdateSha256 empty: GameHost then fetches the companion .sha256 each
    # check (default "<source>.sha256", or set $UpdateSha256Url) and only installs
    # when it differs from what is running. Set $UpdateSha256 instead to pin one
    # fixed version. $AutoCheckHours > 0 enables background checks (0 = button only).
    [string]$UpdateSource = '',
    [string]$UpdateSha256 = '',
    [string]$UpdateSha256Url = '',
    [int]$AutoCheckHours = 0,
    [string]$UpdateUsername = '',
    [string]$UpdatePassword = ''
)

$ErrorActionPreference = 'Stop'

# --- Require administrator ---
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run as Administrator (right-click > Run as administrator, or from an elevated PowerShell).'
}

# --- Resolve and validate the source (published) folder ---
$sourcePath = (Resolve-Path -LiteralPath $SourceDir -ErrorAction Stop).Path
$srcMonitor = Join-Path $sourcePath 'DeviceMon.exe'
$srcWatchdog = Join-Path $sourcePath 'GameHost.exe'
if (-not (Test-Path -LiteralPath $srcMonitor)) { throw "DeviceMon.exe not found in $sourcePath. Run publish.ps1 first." }
if (-not (Test-Path -LiteralPath $srcWatchdog)) { throw "GameHost.exe not found in $sourcePath. Run publish.ps1 first." }

# Guard against a dangerous install target.
if ($InstallDir -eq $env:ProgramFiles -or $InstallDir -eq 'C:\' -or [string]::IsNullOrWhiteSpace($InstallDir)) {
    throw "Refusing to install directly to '$InstallDir'. Use a dedicated subfolder such as '$env:ProgramFiles\DeviceMon'."
}

Write-Host "Installing DeviceMon"
Write-Host "  Source : $sourcePath"
Write-Host "  Target : $InstallDir"
Write-Host ""

# --- Stop running components so their files can be replaced ---
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping $ServiceName service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}
Get-Process -Name DeviceMon, PopupHost, UpdateAgent -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# --- Copy the application into the protected location ---
# Program Files grants standard users read + execute only (no write/delete) by
# default - that inherited permission is exactly what protects the app files, so
# we deliberately do NOT loosen it here.
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
# Strip trailing backslashes: a quoted path ending in "\" (e.g. a spaced source like
# "...\New folder (2)\") makes robocopy read the closing quote as escaped, mangling
# its arguments and returning exit code 16 (copied nothing).
$srcArg = $sourcePath.TrimEnd('\')
$dstArg = $InstallDir.TrimEnd('\')
robocopy $srcArg $dstArg /E /NFL /NDL /NJH /NJS /NP /R:3 /W:2 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "File copy (robocopy) failed with exit code $LASTEXITCODE (source: $srcArg)." }

$watchdogExe = Join-Path $InstallDir 'GameHost.exe'
$monitorExe = Join-Path $InstallDir 'DeviceMon.exe'

# --- Usage database + logs live here and MUST stay user-writable so the child's
#     DeviceMon can record usage. Deletion is separately blocked by GameHost. ---
$dataDir = 'C:\ProgramData\SystemHelper'
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
icacls.exe $dataDir /grant 'Users:(OI)(CI)M' | Out-Null

# --- Optional: configure the trusted update source for quiet SYSTEM updates ---
if (-not [string]::IsNullOrWhiteSpace($UpdateSource)) {
    $isHttps = $UpdateSource -match '^https://'
    # HTTPS with no fixed hash uses the companion .sha256 (explicit or derived), so
    # only a non-HTTPS source (folder/UNC) with no hash is unverifiable.
    if (-not $isHttps -and [string]::IsNullOrWhiteSpace($UpdateSha256)) {
        Write-Host "Note: non-HTTPS source without -UpdateSha256; updates run on the dashboard button only (no version check)."
    }

    $protectedDir = Join-Path $dataDir 'Protected'
    New-Item -ItemType Directory -Path $protectedDir -Force | Out-Null
    # Lock to SYSTEM + Administrators only: the child must not be able to read the
    # source (it may hold credentials) or change where updates are pulled from.
    icacls.exe $protectedDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null

    $cfg = [ordered]@{ source = $UpdateSource }
    if (-not [string]::IsNullOrWhiteSpace($UpdateSha256))    { $cfg.sha256 = $UpdateSha256.ToUpperInvariant() }
    if (-not [string]::IsNullOrWhiteSpace($UpdateSha256Url)) { $cfg.sha256Url = $UpdateSha256Url }
    if ($AutoCheckHours -gt 0)                               { $cfg.autoCheckHours = $AutoCheckHours }
    if (-not [string]::IsNullOrWhiteSpace($UpdateUsername))  { $cfg.username = $UpdateUsername }
    if (-not [string]::IsNullOrWhiteSpace($UpdatePassword))  { $cfg.password = $UpdatePassword }

    ($cfg | ConvertTo-Json) | Out-File -FilePath (Join-Path $protectedDir 'update-source.json') -Encoding utf8 -Force

    # Do NOT seed installed.hash here: this script installs from a folder, and we
    # can't honestly prove it matches the zip published at the source URL. The first
    # check will (re)install the current published build once and UpdateAgent then
    # records the real verified hash, so later checks are accurate. Clear any stale
    # value from a previous install so a genuine update is never skipped.
    $installedHashFile = Join-Path $protectedDir 'installed.hash'
    if (Test-Path -LiteralPath $installedHashFile) { Remove-Item -LiteralPath $installedHashFile -Force -ErrorAction SilentlyContinue }

    $mode = if ($AutoCheckHours -gt 0) { "dashboard button + auto-check every $AutoCheckHours h" } else { "dashboard button" }
    Write-Host "Configured trusted update source (quiet SYSTEM updates: $mode): $UpdateSource"
}

# --- (Re)install the watchdog service pointing at the protected location ---
if ($existing) { sc.exe delete $ServiceName | Out-Null; Start-Sleep -Seconds 2 }
foreach ($legacyName in @('MonitorAndControlWatchdog', 'SystemHelperWatchdog')) {
    $legacy = Get-Service -Name $legacyName -ErrorAction SilentlyContinue
    if ($legacy) {
        Stop-Service -Name $legacyName -Force -ErrorAction SilentlyContinue
        sc.exe delete $legacyName | Out-Null
        Start-Sleep -Seconds 2
    }
}

sc.exe create $ServiceName binPath= "`"$watchdogExe`"" start= auto obj= LocalSystem DisplayName= "GameHost" | Out-Null
sc.exe description $ServiceName "Restarts DeviceMon.exe if it is stopped." | Out-Null
sc.exe failure $ServiceName reset= 60 actions= restart/5000/restart/5000/restart/5000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null
Start-Service -Name $ServiceName

Write-Host ""
Write-Host "Done."
Write-Host "  DeviceMon installed to $InstallDir (protected by Program Files permissions - the child cannot delete or modify it)."
Write-Host "  GameHost watchdog installed and started; it launches DeviceMon in the child's session and restarts it within ~5s if closed."
Write-Host "  Usage data and logs: $dataDir (protected against deletion by GameHost)."
Write-Host ""
Write-Host "To update later: re-run this script as Administrator against the new publish folder."
