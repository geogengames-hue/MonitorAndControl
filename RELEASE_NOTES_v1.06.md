# Monitor & Control v1.06

## Highlights

- **Tamper-resistant data.** `monitor.db` and `events.log` live in the
  SYSTEM-managed `C:\ProgramData\SystemHelper\` directory and are locked by the
  GameHost watchdog (deny-delete ACL + `OWNER RIGHTS` cap) so a standard (child)
  user can no longer delete or rename them - closing DeviceMon no longer allows
  wiping usage history or logs. The watchdog keeps SYSTEM-only backups and restores
  the database (and `DeviceMon.exe`) if either goes missing.
- **Protected install location.** `install.ps1` installs DeviceMon into
  `C:\Program Files\DeviceMon`, where standard users have read-and-execute only -
  the child can close DeviceMon but cannot delete or modify the application files.
- **Remote silent auto-updates.** Point the trusted source at a stable "latest"
  URL and GameHost (SYSTEM) upgrades the child PC on its own - no UAC, no physical
  access. It fetches the companion `.sha256` on each check and installs only when
  the published hash differs from what's running, on the dashboard **Update** button
  and/or a background schedule (`-AutoCheckHours`).
- **Faster relaunch.** Watchdog check interval reduced from 15s to 5s.
- **Broader game discovery.** The dashboard scan now resolves Steam's
  `libraryfolders.vdf` to find games on any drive, and scans Downloads and any
  top-level `Games` folder on every fixed drive for manually-installed games,
  reporting one main executable per folder.
- **Corrupt-config resilience.** A malformed `appsettings.json` no longer crashes
  DeviceMon or the dashboard; it falls back to a last-known-good copy, then defaults.

## Fixes and hardening

- "Clear log" truncates in place instead of deleting, so it works with the
  deny-delete protection; uninstalling GameHost releases the protection so an admin
  can reset the data.
- Update flow hardened: the update-check is rate-limited on every attempt (not only
  successful updates), the dashboard button always installs from the trusted source,
  auto-checks retry soon after a transient failure instead of burning the whole
  interval, and the installed hash is recorded only for verified (HTTPS) packages.
- The companion-hash URL preserves query strings (pre-signed/SAS URLs), and the
  updater writes `installed.hash` to the watchdog's own data directory.
- `install.ps1` auto-detects the published files next to the script and tolerates
  source paths containing spaces (fixes a robocopy failure on such paths).

## Upgrade notes

- Install/upgrade on a child's PC by running `install.ps1` as Administrator
  (`powershell -ExecutionPolicy Bypass -File .\install.ps1 ...`). For remote silent
  updates, add `-UpdateSource "<latest .zip URL>" -AutoCheckHours 24`.
- Existing databases and logs migrate automatically from the old
  `%LOCALAPPDATA%\SystemHelper\` location on first launch.
- Keep `DeviceMon.exe`, `GameHost.exe`, `PopupHost.exe`, and `UpdateAgent.exe`
  together. Tamper protection takes effect once the new `GameHost.exe` is installed.
- Framework-dependent package; requires the .NET 8 Desktop Runtime on the target PC.

## Verification

- Release build completed successfully for all four executables.
- Full regression suite passed (29 tests).
- Deny-delete / owner-rights ACL, corrupt-config startup, game scan, update-check
  throttle, and the robocopy spaced-path fix verified against live runs.
