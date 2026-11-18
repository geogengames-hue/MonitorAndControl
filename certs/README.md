# Code Signing & SmartScreen Reputation

This repository contains **no** signing key and **no** certificate. Builds are signed only when you
supply your own code-signing certificate; otherwise they are **unsigned** (and the scripts say so).
Never commit a `.pfx`/`.p12` file (they are in `.gitignore`).

---

## Why sign?

Windows Defender SmartScreen shows "Windows protected your PC" for executables that:
1. have no valid Authenticode signature, or
2. are signed with a certificate that has not yet gathered enough download/execution reputation.

A signature only proves where a file came from if the private key is secret. Signing with a key anyone
can obtain proves nothing, so there is deliberately **no sample or fallback certificate**.

With a standard **Organization Validation (OV)** certificate the files carry your verified publisher
identity, and SmartScreen reputation builds up over time as users run the signed installer without
malware reports. Once enough reputation exists the warning disappears for later releases signed with that
certificate.

---

## How signing works in this repository

`publish.ps1` signs the four executables and `MonitorAndControlSetup.exe` through `sign.ps1`, using the
certificate you configure through environment variables (or GitHub secrets):

| Secret / environment variable | Description |
|---|---|
| `CODE_SIGN_CERT_BASE64` | Base64-encoded content of your `.pfx` certificate file. |
| `CODE_SIGN_PASSWORD` | Password protecting your `.pfx` file. |
| `CODE_SIGN_CERT_PATH` | *(Optional, local)* Path to a `.pfx` file on disk, instead of `CODE_SIGN_CERT_BASE64`. |

Convert a `.pfx` to Base64 for a GitHub secret:
```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("path\to\your-cert.pfx")) | Set-Clipboard
```

### What happens when no certificate is configured

| Script | Behaviour |
|---|---|
| `sign.ps1` | Fails (or, with `-SkipIfNoCertificate`, warns and leaves the files unsigned). |
| `publish.ps1` | Prints a warning and produces **unsigned** binaries. With `-RequireSigning` it fails instead. `-SkipSigning` skips signing entirely. |
| `release.ps1` | Warns that files are unsigned. With `-RequireSigning` it fails before building if no certificate is configured, and fails if any executable or the installer is unsigned. Use `.\release.ps1 -Upload -RequireSigning` for real releases. |
| CI (`build.yml`) | Passes the two secrets to `publish.ps1`. Forks and pull requests without the secrets get unsigned artifacts. |

### The password is not put on a command line

When `signtool.exe` is used, `sign.ps1` imports the `.pfx` into the current user's certificate store for the
duration of the run, selects it by thumbprint, and removes it afterwards, so other processes cannot read
the password from signtool's arguments. Prefer the environment variables over `-CertPassword` for the same
reason when you start `sign.ps1` from another process.

---

## `sign.ps1`

Signs `.exe`/`.dll` files with SHA-256 and an RFC 3161 timestamp (signtool when available, otherwise
PowerShell `Set-AuthenticodeSignature`).

```powershell
# Using the CODE_SIGN_* environment variables
.\sign.ps1 -Path ".\bin\Release\net8.0-windows\win-x64\installer\MonitorAndControlSetup.exe"

# Using a specific certificate file
.\sign.ps1 -Path ".\publish\DeviceMon.exe" -CertPath "C:\keys\my-cert.pfx" -CertPassword $env:MY_PFX_PASSWORD -Verify
```