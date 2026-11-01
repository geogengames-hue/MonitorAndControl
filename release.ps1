<#
.SYNOPSIS
    Builds, verifies and (optionally) uploads a DeviceMon release in one step.

.DESCRIPTION
    1. Reads the version from the .csproj files and checks all four agree.
    2. Runs the test suite (skip with -SkipTests).
    3. Publishes (publish.ps1) and verifies the build: exe versions, required files,
       valid appsettings.json.
    4. Creates a MATCHED zip + .sha256 pair under dist\<version>\:
         DeviceMon.zip                    (stable name used by the remote auto-update URL)
         MonitorAndControl-v<version>.zip (versioned copy)
       and re-verifies each pair. A mismatched zip/.sha256 is rejected by the SYSTEM
       updater, so this check is the point of the script.
    5. With -Upload: creates the GitHub release for the pushed commit, uploads the four
       files, then downloads them back and confirms the hashes match.

    Nothing is uploaded unless you pass -Upload, and -Upload asks for confirmation.

.EXAMPLE
    .\release.ps1                 # build + verify only
    .\release.ps1 -Upload         # build, verify, then publish the GitHub release
    .\release.ps1 -Upload -WhatIf # show what would be uploaded, change nothing
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Repo = 'geogengames-hue/MonitorAndControl',
    [string]$Title = '',
    [switch]$SkipTests,
    [switch]$Upload
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

function Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Fail($text) { throw "Release stopped: $text" }

# --- 1. Version -------------------------------------------------------------
Step 'Reading version'
$projects = @('MonitorAndControl.csproj', 'Watchdog\SystemHelperWatchdog.csproj', 'PopupHost\PopupHost.csproj', 'UpdateAgent\UpdateAgent.csproj')
$versions = foreach ($p in $projects) {
    $m = [regex]::Match((Get-Content -LiteralPath $p -Raw), '<Version>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*</Version>')
    if (-not $m.Success) { Fail "no <Version> found in $p" }
    [pscustomobject]@{ Project = $p; Version = $m.Groups[1].Value }
}
$version = ($versions | Select-Object -First 1).Version
$mismatch = $versions | Where-Object { $_.Version -ne $version }
if ($mismatch) { Fail ("projects disagree on the version: " + (($versions | ForEach-Object { "$($_.Project)=$($_.Version)" }) -join ', ')) }
$parts = $version.Split('.')
$minor2 = '{0:D2}' -f [int]$parts[1]
# Tag convention used so far: 1.05, 1.06, 1.06.1, 1.07 ... (patch only when non-zero).
$tag = "$($parts[0]).$minor2" + $(if ([int]$parts[2] -gt 0) { ".$($parts[2])" } else { '' })
$notesCandidates = @("RELEASE_NOTES_v$($parts[0]).$minor2.$($parts[2]).md", "RELEASE_NOTES_v$($parts[0]).$minor2.md")
$notes = $notesCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $notes) { Fail "no release notes file found (looked for: $($notesCandidates -join ', '))" }
if (-not $Title) { $Title = "v$tag" }
Write-Host "Version $version   tag $tag   notes $notes"

# --- 2. Git state -----------------------------------------------------------
Step 'Checking git state'
$dirty = @(git status --porcelain)
if ($dirty.Count -gt 0) {
    if ($Upload) { Fail 'uncommitted changes present. Commit and push first so the release matches a real commit.' }
    Write-Warning "Working tree has $($dirty.Count) uncommitted change(s): this is a local test build, not releasable."
}
$commit = (git rev-parse HEAD).Trim()
if ($Upload) {
    git fetch origin --quiet
    $ahead = [int](git rev-list --count 'origin/main..HEAD')
    if ($ahead -gt 0) { Fail "$ahead local commit(s) are not pushed to origin/main. Push first." }
}
Write-Host "Commit $($commit.Substring(0, 7))"

# --- 3. Tests ---------------------------------------------------------------
if ($SkipTests) {
    Write-Warning 'Skipping tests (-SkipTests).'
}
else {
    Step 'Running tests'
    $testOutput = & dotnet run --project Tests\MonitorAndControl.Tests.csproj -c Release 2>&1
    $failed = @($testOutput | Where-Object { $_ -match '^FAIL' })
    $passed = @($testOutput | Where-Object { $_ -match '^PASS' }).Count
    if ($LASTEXITCODE -ne 0 -or $failed.Count -gt 0 -or $passed -eq 0) {
        $failed | ForEach-Object { Write-Host $_ -ForegroundColor Red }
        Fail 'tests did not pass.'
    }
    Write-Host "$passed tests passed."
}

# --- 4. Publish -------------------------------------------------------------
Step 'Publishing'
$publishDir = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\publish'
# publish.ps1 cannot replace exes that are running from the publish folder.
Get-Process -Name DeviceMon, PopupHost, UpdateAgent, GameHost -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($publishDir, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue
$publishOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'publish.ps1') 2>&1
if ($LASTEXITCODE -ne 0) {
    $publishOutput | Select-Object -Last 25 | ForEach-Object { Write-Host $_ }
    Fail 'publish.ps1 failed.'
}

# --- 5. Verify the build ----------------------------------------------------
Step 'Verifying build'
$required = @('DeviceMon.exe', 'GameHost.exe', 'PopupHost.exe', 'UpdateAgent.exe', 'install.ps1', 'appsettings.json', 'wwwroot\index.html')
foreach ($f in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $f))) { Fail "published output is missing $f" }
}
foreach ($exe in 'DeviceMon.exe', 'GameHost.exe', 'PopupHost.exe', 'UpdateAgent.exe') {
    $fileVersion = (Get-Item -LiteralPath (Join-Path $publishDir $exe)).VersionInfo.FileVersion
    if ($fileVersion -ne "$version.0") { Fail "$exe is version $fileVersion, expected $version.0 (stale build?)" }
}
try { $cfg = Get-Content -LiteralPath (Join-Path $publishDir 'appsettings.json') -Raw | ConvertFrom-Json }
catch { Fail "published appsettings.json is not valid JSON: $($_.Exception.Message)" }
Write-Host "All four executables are $version.0; appsettings.json valid (remote dashboard: $($cfg.EnableRemoteDashboard), bind $($cfg.DashboardBindAddress))."

# --- 6. Zip + hash, as matched pairs ---------------------------------------
Step 'Creating packages'
$dist = Join-Path $PSScriptRoot "dist\$version"
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist -Force | Out-Null

$stable = 'DeviceMon.zip'
$versioned = "MonitorAndControl-v$($parts[0]).$minor2.$($parts[2]).zip"
$items = Get-ChildItem -LiteralPath $publishDir | Select-Object -ExpandProperty FullName
Compress-Archive -Path $items -DestinationPath (Join-Path $dist $stable) -CompressionLevel Optimal
Copy-Item -LiteralPath (Join-Path $dist $stable) -Destination (Join-Path $dist $versioned)
foreach ($name in $stable, $versioned) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $dist $name) -Algorithm SHA256).Hash
    # Standard "<hash>  <file>" format; the updater reads the first token.
    [IO.File]::WriteAllText((Join-Path $dist "$name.sha256"), "$hash  $name", [Text.Encoding]::ASCII)
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($name in $stable, $versioned) {
    $zipPath = Join-Path $dist $name
    $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    $recorded = ([IO.File]::ReadAllText("$zipPath.sha256").Trim() -split '\s+')[0]
    if ($actual -ne $recorded) { Fail "$name does not match its .sha256 file." }
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $rootNames = @($zip.Entries | Where-Object { $_.FullName -notmatch '[\\/]' } | ForEach-Object { $_.FullName })
        foreach ($f in 'DeviceMon.exe', 'GameHost.exe', 'UpdateAgent.exe', 'PopupHost.exe', 'install.ps1', 'appsettings.json') {
            if ($rootNames -notcontains $f) { Fail "$name does not contain $f at its root." }
        }
    }
    finally { $zip.Dispose() }
    Write-Host ("OK  {0,-34} {1}" -f $name, $actual)
}
$releaseHash = (Get-FileHash -LiteralPath (Join-Path $dist $stable) -Algorithm SHA256).Hash

# --- 7. Upload (opt-in) -----------------------------------------------------
$files = @($stable, "$stable.sha256", $versioned, "$versioned.sha256") | ForEach-Object { Join-Path $dist $_ }

if (-not $Upload) {
    Step 'Done (nothing uploaded)'
    Write-Host "Packages are in: $dist"
    Write-Host "To publish them as GitHub release '$tag', commit + push, then run:  .\release.ps1 -Upload"
    return
}

Step 'Uploading to GitHub'
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh -and (Test-Path 'C:\Program Files\GitHub CLI\gh.exe')) { $gh = 'C:\Program Files\GitHub CLI\gh.exe' }
if (-not $gh) { Fail 'GitHub CLI (gh) not found. Install it with: winget install GitHub.cli' }
& $gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Fail 'gh is not logged in. Run: gh auth login' }
& $gh release view $tag --repo $Repo 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) { Fail "release '$tag' already exists. Bump the version, or delete that release first." }

if ($PSCmdlet.ShouldProcess("$Repo", "Create PUBLIC release '$tag' ($Title) from commit $($commit.Substring(0, 7)) with $($files.Count) files")) {
    & $gh release create $tag @files --repo $Repo --target $commit --title $Title --notes-file $notes --latest
    if ($LASTEXITCODE -ne 0) { Fail 'gh release create failed.' }

    Step 'Verifying the uploaded release'
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("release-check-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $base = "https://github.com/$Repo/releases/download/$tag"
        Invoke-WebRequest -Uri "$base/$stable" -OutFile (Join-Path $tmp $stable) -UseBasicParsing
        Invoke-WebRequest -Uri "$base/$stable.sha256" -OutFile (Join-Path $tmp "$stable.sha256") -UseBasicParsing
        $remoteActual = (Get-FileHash -LiteralPath (Join-Path $tmp $stable) -Algorithm SHA256).Hash
        $remoteRecorded = ([IO.File]::ReadAllText((Join-Path $tmp "$stable.sha256")).Trim() -split '\s+')[0]
        if ($remoteActual -ne $remoteRecorded -or $remoteActual -ne $releaseHash) {
            Fail "uploaded files do not match! zip=$remoteActual published=$remoteRecorded local=$releaseHash. Fix or delete release '$tag' before devices update."
        }
        Write-Host "Uploaded $stable verified: $remoteActual" -ForegroundColor Green
    }
    finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}
