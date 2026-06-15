# Publish MonitorAndControl as framework-dependent (2 MB)
# Requires .NET 8 Runtime on target PC:
# https://dotnet.microsoft.com/en-us/download/dotnet/8.0

$publishDir = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\publish'

dotnet publish .\MonitorAndControl.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
dotnet publish .\Watchdog\SystemHelperWatchdog.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $publishDir
dotnet publish .\PopupHost\PopupHost.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $publishDir

Write-Host ""
Write-Host "Published to: $publishDir"
Write-Host "Total size: $((Get-ChildItem -Recurse -File $publishDir | Measure-Object -Property Length -Sum).Sum / 1KB) KB"
Write-Host ""
Write-Host "Copy the 'publish' folder to the child's PC and run SystemHelper.exe"
Write-Host "To install watchdog on the child's PC, run install-watchdog.ps1 as Administrator."
