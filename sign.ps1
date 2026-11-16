<#
.SYNOPSIS
    Signs executables and libraries with Authenticode Code Signing (SHA256 + RFC 3161 Timestamp).

.DESCRIPTION
    Uses a certificate you supply: -CertPath / -CertBase64 / -CertPassword, or the environment variables
    CODE_SIGN_CERT_BASE64 (or CODE_SIGN_CERT_PATH) and CODE_SIGN_PASSWORD.
    There is NO built-in or fallback certificate. Without one this script fails, or, with
    -SkipIfNoCertificate, prints a warning and leaves the files unsigned.
    Prioritizes signtool.exe when available, falling back to PowerShell Set-AuthenticodeSignature.
    With signtool the certificate is imported into a temporary entry of the current user's store and
    selected by thumbprint, so the password never appears on a command line.

.EXAMPLE
    .\sign.ps1 -Path ".\publish\DeviceMon.exe"
    .\sign.ps1 -Path (Get-ChildItem .\publish\*.exe)
    .\sign.ps1 -Path ".\Installer\bin\Release\net8.0-windows\win-x64\MonitorAndControlSetup.exe" -Verify
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, ValueFromPipeline = $true, ValueFromPipelineByPropertyName = $true, Position = 0)]
    [Alias('FilePath', 'FullName')]
    [string[]]$Path,

    [string]$CertPath,
    [string]$CertPassword,
    [string]$CertBase64,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$Description = 'Monitor and Control',
    [switch]$SkipTimestamp,
    [switch]$SkipIfNoCertificate,
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'

function Find-SignTool {
    $cmd = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $kitsRoots = @(
        "C:\Program Files (x86)\Windows Kits\10\bin",
        "C:\Program Files\Windows Kits\10\bin",
        "C:\Program Files (x86)\Microsoft SDKs\Windows",
        "C:\Program Files\Microsoft Visual Studio"
    )

    foreach ($root in $kitsRoots) {
        if (Test-Path -LiteralPath $root) {
            $found = Get-ChildItem -Path $root -Filter 'signtool.exe' -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match 'x64' } |
                Select-Object -First 1
            if ($found) { return $found.FullName }
        }
    }
    return $null
}

# Resolve certificate & password
$tempPfxPath = $null
$effectiveCertPath = $CertPath
$effectivePassword = $CertPassword

if (-not $effectiveCertPath -and -not $CertBase64) {
    if ($env:CODE_SIGN_CERT_BASE64) {
        $CertBase64 = $env:CODE_SIGN_CERT_BASE64
    }
    elseif ($env:CODE_SIGN_CERT_PATH) {
        $effectiveCertPath = $env:CODE_SIGN_CERT_PATH
    }
}

if (-not $effectivePassword) {
    if ($env:CODE_SIGN_PASSWORD) {
        $effectivePassword = $env:CODE_SIGN_PASSWORD
    }
    elseif ($env:CODE_SIGN_CERT_PASSWORD) {
        $effectivePassword = $env:CODE_SIGN_CERT_PASSWORD
    }
}

if ($CertBase64) {
    $tempPfxPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "code-signing-$([System.Guid]::NewGuid().ToString('N')).pfx")
    $certBytes = [System.Convert]::FromBase64String($CertBase64)
    [System.IO.File]::WriteAllBytes($tempPfxPath, $certBytes)
    $effectiveCertPath = $tempPfxPath
}

if (-not $effectiveCertPath) {
    $message = "No code-signing certificate configured (set CODE_SIGN_CERT_BASE64 and CODE_SIGN_PASSWORD, or CODE_SIGN_CERT_PATH, or pass -CertPath). The files are NOT signed."
    if ($SkipIfNoCertificate) {
        Write-Warning $message
        return
    }
    throw $message
}

if (-not (Test-Path -LiteralPath $effectiveCertPath)) {
    throw "Certificate file not found at '$effectiveCertPath'."
}

$importedThumbprint = $null
try {
    # Resolve all target files
    $filesToSign = @()
    foreach ($p in $Path) {
        $resolved = Resolve-Path -LiteralPath $p -ErrorAction Stop
        foreach ($r in $resolved) {
            if (Test-Path -LiteralPath $r.Path -PathType Leaf) {
                $filesToSign += $r.Path
            }
        }
    }

    if ($filesToSign.Count -eq 0) {
        Write-Warning "No files to sign."
        return
    }

    $signtool = Find-SignTool
    $fallbackTimestamps = @($TimestampUrl, 'http://timestamp.sectigo.com', 'http://timestamp.identrust.com')

    if ($signtool) {
        Write-Host "Using SignTool: $signtool" -ForegroundColor Cyan

        # Do not pass the password to signtool (/p would expose it to other processes on the machine):
        # import the PFX into the current user's store for the duration of this run and select it by thumbprint.
        $securePassword = if ($effectivePassword) { ConvertTo-SecureString -String $effectivePassword -AsPlainText -Force } else { $null }
        $probe = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($effectiveCertPath, $securePassword)
        $thumbprint = $probe.Thumbprint
        $probe.Dispose()
        if (-not (Test-Path -LiteralPath "Cert:\CurrentUser\My\$thumbprint")) {
            $importArgs = @{ FilePath = $effectiveCertPath; CertStoreLocation = 'Cert:\CurrentUser\My' }
            if ($securePassword) { $importArgs.Password = $securePassword }
            Import-PfxCertificate @importArgs | Out-Null
            $importedThumbprint = $thumbprint
        }

        foreach ($file in $filesToSign) {
            $signed = $false
            if (-not $SkipTimestamp) {
                foreach ($tsUrl in $fallbackTimestamps) {
                    $signArgs = @('sign', '/sha1', $thumbprint, '/s', 'My')
                    $signArgs += @('/fd', 'SHA256', '/tr', $tsUrl, '/td', 'SHA256', '/d', $Description, $file)

                    $output = & $signtool @signArgs 2>&1
                    if ($LASTEXITCODE -eq 0) {
                        $signed = $true
                        break
                    }
                    else {
                        Write-Warning "SignTool timestamp with $tsUrl failed on $file. Retrying..."
                    }
                }
            }

            if (-not $signed) {
                # Fallback to signing without timestamp
                $signArgs = @('sign', '/sha1', $thumbprint, '/s', 'My')
                $signArgs += @('/fd', 'SHA256', '/d', $Description, $file)

                $output = & $signtool @signArgs 2>&1
                if ($LASTEXITCODE -ne 0) {
                    throw "SignTool failed to sign $file (exit code $LASTEXITCODE): $($output -join "`n")"
                }
                if (-not $SkipTimestamp) {
                    Write-Warning "Signed $file without RFC 3161 timestamp (timestamp servers unreachable)."
                }
            }
        }
    }
    else {
        Write-Host "SignTool not found; using PowerShell Set-AuthenticodeSignature" -ForegroundColor Cyan
        $x509Cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $effectiveCertPath,
            $effectivePassword,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
        )

        foreach ($file in $filesToSign) {
            $sig = $null
            if (-not $SkipTimestamp) {
                foreach ($tsUrl in $fallbackTimestamps) {
                    try {
                        $sig = Set-AuthenticodeSignature -FilePath $file -Certificate $x509Cert -HashAlgorithm SHA256 -TimestampServer $tsUrl -ErrorAction Stop
                        if ($sig.Status -ne 'UnknownError' -or $sig.SignerCertificate) {
                            break
                        }
                    }
                    catch {
                        Write-Warning "PowerShell timestamp with $tsUrl failed on $($file): $($_.Exception.Message)"
                    }
                }
            }

            if (-not $sig -or -not $sig.SignerCertificate) {
                $sig = Set-AuthenticodeSignature -FilePath $file -Certificate $x509Cert -HashAlgorithm SHA256 -ErrorAction Stop
                if (-not $SkipTimestamp) {
                    Write-Warning "Signed $file without timestamp."
                }
            }

            if (-not $sig.SignerCertificate) {
                throw "Set-AuthenticodeSignature failed to sign $($file): $($sig.StatusMessage)"
            }
        }
    }

    # Verification & reporting
    Write-Host "Code signing completed successfully:" -ForegroundColor Green
    foreach ($file in $filesToSign) {
        $sig = Get-AuthenticodeSignature -LiteralPath $file
        $subject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '<unsigned>' }
        $status = $sig.Status
        Write-Host "  [Signed] $([System.IO.Path]::GetFileName($file))" -ForegroundColor Green
        Write-Host "           Signer: $subject" -ForegroundColor Gray
        Write-Host "           Status: $status" -ForegroundColor Gray
        if ($sig.TimeStamperCertificate) {
            Write-Host "           Timestamp: $($sig.TimeStamperCertificate.Subject)" -ForegroundColor Gray
        }

        if (-not $sig.SignerCertificate) {
            throw "Verification failed: $file is not signed."
        }
        if ($Verify -and $status -ne 'Valid') {
            Write-Warning "Verification note: $file status is $status ($($sig.StatusMessage))"
        }
    }
}
finally {
    if ($importedThumbprint -and (Test-Path -LiteralPath "Cert:\CurrentUser\My\$importedThumbprint")) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$importedThumbprint" -DeleteKey -ErrorAction SilentlyContinue
    }

    if ($tempPfxPath -and (Test-Path -LiteralPath $tempPfxPath)) {
        Remove-Item -LiteralPath $tempPfxPath -Force -ErrorAction SilentlyContinue
    }
}
