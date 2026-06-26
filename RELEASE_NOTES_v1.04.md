# Monitor & Control v1.04

## Highlights

- Refined app update flow and installer robustness.
- Improved watchdog repair and restart handling during updates.
- Strengthened update ZIP validation and error reporting.
- Minor UI/translation polish and stability fixes.

## Fixes and hardening

- Fixed rare watchdog restart failures when the app is closing.
- Ensured update failure reasons are preserved in the dashboard update status.
- Hardened startup for cases where the dashboard window is recreated after a crash.
- Fixed update package checksum reporting for remote HTTPS ZIP sources.

## Upgrade notes

- Existing SQLite databases are migrated automatically.
- Keep `DeviceMon.exe`, `GameHost.exe`, `PopupHost.exe`, and `UpdateAgent.exe` together when replacing an installation.
- This framework-dependent package requires the .NET 8 Desktop Runtime on the target PC.

## Verification

- Release build completed successfully.
- JavaScript and translation JSON files validated.
- Full regression suite passed.
