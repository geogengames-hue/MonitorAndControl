# Monitor & Control v1.05

## Highlights

- **Tamper-resistant data.** `monitor.db` and `events.log` now live in the
  SYSTEM-managed `C:\ProgramData\SystemHelper\` directory and are locked by the
  GameHost watchdog so a standard (child) user can no longer delete or rename
  them — closing DeviceMon no longer allows wiping usage history or logs.
- **Automatic recovery.** The watchdog keeps SYSTEM-only backups under
  `C:\ProgramData\SystemHelper\Protected\` and restores the database (and
  `DeviceMon.exe`) if either goes missing.
- **Faster relaunch.** The watchdog check interval was reduced from 15s to 5s,
  shrinking the window in which a manually closed monitor stays down.
- **Protected install location.** New `install.ps1` installs DeviceMon into
  `C:\Program Files\DeviceMon`, where standard users have read-and-execute only -
  the child can close DeviceMon but cannot delete or modify the application files
  (`.exe`s, DLLs, `appsettings.json`). Re-run `install.ps1` as admin to update.
- **Quiet remote updates for protected installs.** When a trusted update source is
  configured (`install.ps1 -UpdateSource ...`), the `GameHost` service (SYSTEM)
  performs updates - no UAC prompt, works remotely, installs into the protected
  folder, and relaunches DeviceMon in the child's session. The source is stored in
  a SYSTEM-only file the child cannot read or redirect.
- **Auto-update to new versions.** Point the source at a stable "latest" URL and
  GameHost fetches the companion `.sha256` on each check, installing only when the
  hash differs from what is running. Triggered by the dashboard **Update** button
  and/or a background schedule (`-AutoCheckHours`). This lets you publish a new
  release and have child PCs upgrade themselves without physical access.
- **Corrupt-config resilience.** A malformed `appsettings.json` no longer crashes
  DeviceMon or takes down the dashboard. Configuration now falls back to a
  last-known-good backup (and then built-in defaults), and the web host no longer
  aborts on an unparseable config file.

## Fixes and hardening

- Data files are protected with an explicit deny-delete ACL plus an `OWNER RIGHTS`
  cap, so the owning user cannot re-grant themselves delete permission.
- "Clear log" now truncates the log in place instead of deleting the file, so it
  works alongside the deny-delete protection.
- Executable and data restore are skipped while an update is in progress, so an
  update never fights the watchdog.
- Uninstalling GameHost automatically releases the protection so an administrator
  can reset or manage the data.
- A valid configuration is copied to a protected last-known-good file on every
  successful load, so a corrupted `appsettings.json` self-recovers the real
  settings instead of silently reverting to defaults.

## Upgrade notes

- For a child's PC, install with `install.ps1` (as Administrator) so the app lives
  in the protected `C:\Program Files\DeviceMon` folder. Update the same way -
  re-run `install.ps1`. The in-dashboard update flow only applies to non-protected
  installs, since it writes into the application folder.
- Existing databases and logs are migrated automatically from the old
  `%LOCALAPPDATA%\SystemHelper\` location on first launch of this version.
- Keep `DeviceMon.exe`, `GameHost.exe`, `PopupHost.exe`, and `UpdateAgent.exe`
  together when replacing an installation. The tamper protection only takes effect
  once the **new `GameHost.exe`** is in place — after updating, confirm the update
  log does not report `Skipped locked watchdog file: GameHost.exe`.
- This framework-dependent package requires the .NET 8 Desktop Runtime on the
  target PC.

## Verification

- Release build completed successfully for all four executables.
- Full regression suite passed (28 tests).
- Deny-delete / owner-rights ACL verified against a live file.
