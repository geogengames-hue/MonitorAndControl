# Monitor & Control v1.03

## Highlights

- Added shared limit groups so several apps can consume one combined daily allowance, such as a single Games limit.
- Added group usage totals to Today and History views.
- Added per-app foreground/background tracking controls directly in the Limits table.
- Added optional background usage counting for tracked apps, including communication apps and other microphone-capable apps.
- Added overlay-focus filtering so voice/chat overlays do not steal counted time from the real foreground app.
- Added live tracking diagnostics so the dashboard explains whether a process is counted, ignored, background-counted, overlay-filtered, locked, or idle-paused.
- Added optional scheduled parent summaries by day, week, or month, with next-start catch-up when the computer was off.
- Added stronger tamper handling for missing data/configuration, watchdog failure/recovery, forced DeviceMon closure, clock jumps, and repeated dashboard login failures.
- Improved email command handling so broadcast commands such as `mc: status` are processed once by every configured computer using the same inbox.
- Redesigned the dashboard with a richer command-center layout, improved cards, buttons, motion, responsive behavior, and a cleaner backup/restore picker.
- Added update result reporting in Settings, including success/failure status, timestamps, and the latest update log lines after the dashboard restarts.

## Fixes and hardening

- Removed limits and schedules now stay removed after restart instead of being recreated from `appsettings.json`.
- Reset Today also clears pending unflushed usage so deleted seconds do not reappear.
- Forgetting an app also removes stale group membership.
- Group-limit breaches now avoid warning every group member when only the active/running app needs enforcement.
- Config import preserves local usage history instead of deleting historical group records.
- Updater now rejects HTTP update URLs and requires HTTPS ZIP sources with an exact SHA-256 hash.
- Watchdog update-marker permissions were tightened so normal users cannot suppress watchdog restarts by touching the marker.
- Email tamper recovery alerts are separated from normal app-start alerts so watchdog restarts are easier to identify.
- Improved concurrency safety around known-app tracking data and email command processing.
- Updater now runs as a silent, windowless helper without a UAC prompt; if Windows permissions block part of the update, the dashboard records the failure in the update status.
- Failed updates now record a visible error status and try to restart DeviceMon so the parent can read the failure reason.

## Upgrade notes

- Existing SQLite databases are migrated automatically.
- Existing historical totals remain local and are preserved during config import.
- Keep `DeviceMon.exe`, `GameHost.exe`, `PopupHost.exe`, and `UpdateAgent.exe` together when replacing an installation.
- This framework-dependent package requires the .NET 8 Desktop Runtime on the target PC.
- After replacing files, start `DeviceMon.exe`; it will repair or update the watchdog service when permissions allow.

## Verification

- Release build completed successfully.
- JavaScript and translation JSON files validated.
- Full regression suite passed.
