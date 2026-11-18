# Monitor & Control v1.13.1

## Highlights

- **One-file installer: `MonitorAndControlSetup.exe`.** For a new PC, download this one file
  and run it. It asks for administrator rights once, installs the .NET 8 runtimes from
  Microsoft if they are missing (it only runs them if Microsoft's signature checks out),
  installs DeviceMon into `C:\Program Files\DeviceMon`, and installs and starts the GameHost
  service. It shows a progress window in the PC's language (English, German, Spanish, French
  or Russian). Thanks to @TheBullRing for the contribution.
- **Your settings are kept.** Running Setup over an existing install keeps your
  `appsettings.json`. If the new version adds options your file lacks, Setup asks whether to
  add them (recommended), keep your file as it is, or use the new default file. A dated backup
  is always written first.
- **Unattended installs:** add `-Quiet` (or `-AppSettings Merge|Keep|Replace`) and Setup
  asks nothing and closes when done. Other `install.ps1` options can be passed too, for
  example `-InstallDir "D:\DeviceMon"`.

## Updates on PCs that already have the app

- **Nothing changes.** Installed PCs keep updating themselves the usual way from
  `DeviceMon.zip`, which is still published with every release and has the same contents.
  The installer is not part of that zip.
- The manual install (download the zip, run `install.ps1`) still works as before.

## For maintainers

- Optional code signing: set `CODE_SIGN_CERT_BASE64` and `CODE_SIGN_PASSWORD` (or
  `CODE_SIGN_CERT_PATH`) to sign the programs and the installer with your own certificate.
  Without one the build is unsigned, as before, with a warning. `release.ps1 -RequireSigning`
  refuses to build without a certificate.
- The version now also lives in `Installer\Installer.csproj`; `release.ps1` checks all five
  projects match.

## Upgrade notes

- No changes to how DeviceMon, PC Time, limits or the dashboard work.
- The programs and the installer are not code-signed, so Windows may show "Windows
  protected your PC" when Setup is run. Choose **More info > Run anyway**.
- Not yet verified on a real install: the new installer. Test it on a spare PC or in
  Windows Sandbox before using it on a child's PC.
