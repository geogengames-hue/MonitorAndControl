# Publish DeviceMon as framework-dependent (2 MB)
# Requires .NET 8 Runtime on target PC:
# https://dotnet.microsoft.com/en-us/download/dotnet/8.0

$ErrorActionPreference = 'Stop'

$publishDir = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\publish'

# Clean stale obj dirs to avoid duplicate assembly attribute issues
Get-ChildItem -Path $PSScriptRoot -Recurse -Directory -Filter obj -Depth 1 | Remove-Item -Recurse -Force
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

dotnet publish .\MonitorAndControl.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "DeviceMon publish failed with exit code $LASTEXITCODE." }

dotnet publish .\Watchdog\SystemHelperWatchdog.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Watchdog publish failed with exit code $LASTEXITCODE." }

dotnet publish .\PopupHost\PopupHost.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "PopupHost publish failed with exit code $LASTEXITCODE." }

dotnet publish .\UpdateAgent\UpdateAgent.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "UpdateAgent publish failed with exit code $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination $publishDir -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-watchdog.ps1') -Destination $publishDir -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall-watchdog.ps1') -Destination $publishDir -Force

# Single-file installer: embeds the whole publish folder so the customer only needs
# MonitorAndControlSetup.exe. It is written next to (not into) the publish folder so it is
# not shipped inside DeviceMon.zip.
$installerDir = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\installer'
$payloadZip = Join-Path ([IO.Path]::GetTempPath()) 'MonitorAndControlSetup-payload.zip'
if (Test-Path -LiteralPath $installerDir) { Remove-Item -LiteralPath $installerDir -Recurse -Force }
if (Test-Path -LiteralPath $payloadZip) { Remove-Item -LiteralPath $payloadZip -Force }
Compress-Archive -Path (Get-ChildItem -LiteralPath $publishDir | Select-Object -ExpandProperty FullName) -DestinationPath $payloadZip -CompressionLevel Optimal
try {
    dotnet publish .\Installer\Installer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true "-p:PayloadZip=$payloadZip" -o $installerDir
    if ($LASTEXITCODE -ne 0) { throw "Installer publish failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item -LiteralPath $payloadZip -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Published to: $publishDir"
Write-Host "Total size: $((Get-ChildItem -Recurse -File $publishDir | Measure-Object -Property Length -Sum).Sum / 1KB) KB"
Write-Host "Single-file installer: $(Join-Path $installerDir 'MonitorAndControlSetup.exe')"
Write-Host ""
Write-Host "Copy the 'publish' folder to the child's PC and run DeviceMon.exe"
Write-Host "To install watchdog on the child's PC, run install-watchdog.ps1 as Administrator."
