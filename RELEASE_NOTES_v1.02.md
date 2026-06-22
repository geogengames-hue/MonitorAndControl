# Monitor & Control v1.02

## Highlights

- Optional per-process background usage tracking with concurrent foreground/background accounting.
- Voice and notification overlay focus filtering for communication apps.
- Foreground, background, total, and legacy usage breakdowns in Today and History.
- Tracking diagnostics explaining why each running process is counted, ignored, or paused.
- Tracking automatically pauses while Windows is locked, with optional configurable idle suspension.
- Limits now close every running executable mapped to the same application.
- Optional daily, weekly, or monthly parent email summaries with next-start catch-up.
- Optional tamper alerts for missing data/configuration, clock jumps, watchdog failure, and repeated login failures.
- Hardened remote dashboard login with HTTP-only session cookies and explicit logout.
- Improved process enumeration efficiency and Limits table layout.
- Updated German, Spanish, French, and Russian translations.

## Upgrade notes

- Existing SQLite databases are migrated automatically.
- Existing historical totals remain accurate but appear as **Legacy / unclassified** in source-breakdown views.
- Limits and schedules removed from the dashboard are no longer recreated from `appsettings.json` after restart.
- Idle suspension, scheduled summaries, and tamper alerts are disabled by default.
- Scheduled summaries require configured Gmail credentials. Tamper alerts use configured email and webhook channels.
- Keep `DeviceMon.exe`, `GameHost.exe`, `PopupHost.exe`, and `UpdateAgent.exe` together when replacing an installation.
- This framework-dependent package requires the .NET 8 Desktop Runtime on the target PC.

## Verification

- Release build completed with zero warnings.
- JavaScript and all translation JSON files validated.
- Full regression suite passed.
